// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Persistence;

public enum JournalImportOutcome
{
    /// <summary>The protected journal already exists. The old file is not opened.</summary>
    AlreadyMoved,
    /// <summary>There was no old file; a new, empty journal was made.</summary>
    Fresh,
    /// <summary>The rows of the old file that passed the checks were copied.</summary>
    Imported,
    /// <summary>The old file could not be read as a journal; a new, empty journal was made and nothing was copied.</summary>
    LegacyUnreadable,
}

/// <summary>What the one-time move of the journal did. <see cref="LegacyMovedTo"/> is the new name of the old file, or null when it could not be renamed.</summary>
public sealed record JournalImportResult(JournalImportOutcome Outcome, int Imported, int Skipped, string? LegacyMovedTo);

/// <summary>
/// The one-time move of the journal from the user's own data folder to the admin-only journals folder. The old file could have been written by any program of the user, so it is
/// never opened in place and never trusted again: it is copied into the protected folder, checked, and only rows that pass every check are written into a new journal. A row passes when
/// it is finished (not active, not quarantined), its payload reads back as a current record of the same id, its columns agree with the payload and, for a setting row, its setting is in
/// the catalog. Active rows are left out, because recovery would act on them. Events are copied only for rows that were copied. The new journal is moved into place in one step, so a
/// crash leaves either no protected journal (the move runs again) or a complete one. The old file is then renamed to <c>&lt;name&gt;.untrusted-&lt;UTC time&gt;</c>.
/// </summary>
public sealed partial class SqliteOperationStore
{
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PreferenceKey();

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventCode();

    /// <param name="legacyPath">The old journal (<c>%LocalAppData%\Winora\simulation.db</c>).</param>
    /// <param name="targetPath">The new journal in the admin-only folder. Its folder must exist and be protected; the caller checks that.</param>
    /// <param name="knownSetting">True for a setting id of the catalog. Null accepts every id.</param>
    public static JournalImportResult MoveLegacy(string legacyPath, string targetPath, Func<string, bool>? knownSetting = null, DateTimeOffset? now = null)
    {
        var target = Path.GetFullPath(targetPath);
        if (File.Exists(target)) return new(JournalImportOutcome.AlreadyMoved, 0, 0, null);
        var legacy = Path.GetFullPath(legacyPath);
        var staging = target + ".import";
        var copy = target + ".legacy-copy";
        DeleteDatabase(staging);
        DeleteDatabase(copy);
        var fresh = new SqliteOperationStore(staging);
        var outcome = JournalImportOutcome.Fresh;
        int imported = 0, skipped = 0;
        if (IsPlainFile(legacy))
        {
            try
            {
                File.Copy(legacy, copy, overwrite: false);
                var source = new SqliteOperationStore(copy);
                (imported, skipped) = source.CopyTrustedRowsTo(fresh, knownSetting ?? (static _ => true), now ?? DateTimeOffset.UtcNow);
                outcome = JournalImportOutcome.Imported;
            }
            catch (Exception e) when (e is InvalidDataException or StoreException or SqliteException or IOException or UnauthorizedAccessException or OperationException)
            {
                // Nothing is copied from a file that is not a sound journal. The new journal starts empty.
                DeleteDatabase(staging);
                fresh = new SqliteOperationStore(staging);
                (outcome, imported, skipped) = (JournalImportOutcome.LegacyUnreadable, 0, 0);
            }
            finally
            {
                DeleteDatabase(copy);
                foreach (var backup in Directory.EnumerateFiles(Path.GetDirectoryName(copy)!, Path.GetFileName(copy) + ".v*.bak")) TryDelete(backup);
            }
        }
        SqliteConnection.ClearAllPools();
        File.Move(staging, target, overwrite: false);
        TryDelete(staging + "-journal");
        string? movedTo = null;
        if (File.Exists(legacy))
        {
            var stamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var aside = legacy + ".untrusted-" + stamp;
            for (var i = 2; File.Exists(aside); i++) aside = legacy + ".untrusted-" + stamp + "-" + i.ToString(CultureInfo.InvariantCulture);
            try
            {
                File.Move(legacy, aside, overwrite: false);
                movedTo = aside;
                if (File.Exists(legacy + "-journal")) File.Move(legacy + "-journal", aside + "-journal", overwrite: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the protected journal exists now, so the old file is never read again */ }
        }
        return new(outcome, imported, skipped, movedTo);
    }

    private static bool IsPlainFile(string path)
    {
        try { return File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void DeleteDatabase(string path)
    {
        TryDelete(path);
        TryDelete(path + "-journal");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* left for the next start */ }
    }

    private (int Imported, int Skipped) CopyTrustedRowsTo(SqliteOperationStore target, Func<string, bool> knownSetting, DateTimeOffset now)
    {
        var latest = now.AddDays(1);
        return Read(source =>
        {
            if (!string.Equals(Scalar(source, "PRAGMA integrity_check;") as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
            return target.Write("import", destination =>
            {
                int imported = 0, skipped = 0;
                void Count(bool kept) { if (kept) imported++; else skipped++; }

                using (var command = Command(source, "SELECT id,payload,rev,state,compensation_prepared,setting_id,quarantined,created_at_utc FROM setting_operations ORDER BY rowid;", []))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var (id, payload, rev) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
                        var (record, fault) = MaterializeSetting(id, payload, rev);
                        var keep = fault is null && record is not null && reader.GetInt32(6) == 0 && (int)record.State == reader.GetInt32(3) && record.CompensationPrepared == (reader.GetInt32(4) == 1)
                            && !OperationTransitions.IsActive(record.State, record.CompensationPrepared) && record.SettingId == reader.GetString(5) && knownSetting(record.SettingId)
                            && record.CreatedAtUtc <= latest && record.UpdatedAtUtc <= latest;
                        if (keep)
                        {
                            Execute(destination, "INSERT INTO setting_operations(id,payload,rev,state,compensation_prepared,setting_id,quarantined,created_at_utc) VALUES($id,$payload,$rev,$state,0,$setting,0,$created);",
                                ("$id", id), ("$payload", payload), ("$rev", rev), ("$state", (int)record!.State), ("$setting", record.SettingId), ("$created", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
                            CopyEvents(source, destination, "setting_events", id, machine: false);
                        }
                        Count(keep);
                    }

                using (var command = Command(source, "SELECT id,payload,rev,state,compensation_prepared,compensation_committed,definition_id,quarantined,created_at_utc FROM machine_operations ORDER BY rowid;", []))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var (id, payload, rev) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
                        var (record, fault) = MaterializeMachine(id, payload, rev);
                        var keep = fault is null && record is not null && reader.GetInt32(7) == 0 && (int)record.State == reader.GetInt32(3) && record.CompensationPrepared == (reader.GetInt32(4) == 1)
                            && record.CompensationCommitted == (reader.GetInt32(5) == 1) && !MachineTransitions.IsActive(record.State, record.CompensationPrepared) && !record.CompensationCommitted
                            && record.DefinitionId == reader.GetString(6) && record.CreatedAtUtc <= latest && record.UpdatedAtUtc <= latest;
                        if (keep)
                        {
                            Execute(destination, "INSERT INTO machine_operations(id,payload,rev,state,compensation_prepared,compensation_committed,definition_id,quarantined,created_at_utc) VALUES($id,$payload,$rev,$state,0,0,$definition,0,$created);",
                                ("$id", id), ("$payload", payload), ("$rev", rev), ("$state", (int)record!.State), ("$definition", record.DefinitionId), ("$created", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
                            CopyEvents(source, destination, "machine_events", id, machine: true);
                        }
                        Count(keep);
                    }

                using (var command = Command(source, "SELECT id,payload,rev,state,compensation_prepared,resource_id,quarantined,created_at_utc FROM operations ORDER BY rowid;", []))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var (id, payload, rev) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
                        var (record, fault) = Materialize(id, payload, rev);
                        var keep = fault is null && record is not null && reader.GetInt32(6) == 0 && (int)record.State == reader.GetInt32(3) && record.CompensationPrepared == (reader.GetInt32(4) == 1)
                            && !OperationTransitions.IsActive(record.State, record.CompensationPrepared) && record.ResourceId == reader.GetString(5) && record.CreatedAtUtc <= latest;
                        if (keep)
                        {
                            Execute(destination, "INSERT INTO operations(id,payload,rev,state,compensation_prepared,resource_id,quarantined,created_at_utc) VALUES($id,$payload,$rev,$state,0,$resource,0,$created);",
                                ("$id", id), ("$payload", payload), ("$rev", rev), ("$state", (int)record!.State), ("$resource", record.ResourceId), ("$created", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
                            CopyEvents(source, destination, "op_events", id, machine: false);
                        }
                        Count(keep);
                    }

                var resource = ReadResource(source);
                Execute(destination, "UPDATE simulated_resource SET value=$value, revision=$revision WHERE id=1;", ("$value", resource.Value ? 1 : 0), ("$revision", Math.Max(0, resource.Revision)));

                using (var command = Command(source, "SELECT key,value FROM preferences;", []))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetFieldType(1) != typeof(string)) continue;
                        var (key, value) = (reader.GetString(0), reader.GetString(1));
                        if (!PreferenceKey().IsMatch(key) || value.Length > 4096 || value.Any(char.IsControl)) continue;
                        Execute(destination, "INSERT INTO preferences(key,value) VALUES($key,$value);", ("$key", key), ("$value", value));
                    }
                return (imported, skipped);
            });
        });
    }

    // The ledger of one copied row, in its order. An event that does not have the shape the store writes ends the copy of that row's ledger.
    private static void CopyEvents(SqliteConnection source, SqliteConnection destination, string table, string id, bool machine)
    {
        var columns = machine ? "op_id,op_rev,from_state,from_comp,from_commit,to_state,to_comp,to_commit,code,at_utc" : "op_id,op_rev,from_state,from_comp,to_state,to_comp,code,at_utc";
        var values = machine ? "$op,$rev,$fs,$fc,$fm,$ts,$tc,$tm,$code,$at" : "$op,$rev,$fs,$fc,$ts,$tc,$code,$at";
        using var command = Command(source, $"SELECT {columns} FROM {table} WHERE op_id=$id ORDER BY seq;", [("$id", id)]);
        using var reader = command.ExecuteReader();
        static bool Flag(object value) => value is DBNull || value is long l && l is 0 or 1;
        static bool State(object value) => value is DBNull || value is long l && Enum.IsDefined((OperationState)l);
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            var (code, at) = (row[^2], row[^1]);
            var shape = row[1] is long and >= 0 && code is string c && EventCode().IsMatch(c) && at is string a && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                && (machine
                    ? State(row[2]) && Flag(row[3]) && Flag(row[4]) && row[5] is long && State(row[5]) && row[6] is long && Flag(row[6]) && row[7] is long && Flag(row[7])
                    : State(row[2]) && Flag(row[3]) && row[4] is long && State(row[4]) && row[5] is long && Flag(row[5]));
            if (!shape) return;
            var names = values.Split(',');
            Execute(destination, $"INSERT INTO {table}({columns}) VALUES({values});", [.. names.Select((n, i) => (n, (object?)row[i]))]);
        }
    }
}
