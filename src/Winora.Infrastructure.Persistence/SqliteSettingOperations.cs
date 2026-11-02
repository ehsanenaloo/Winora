// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// Registry-setting journal (schema v3). Same invariants as the simulation journal: BEGIN IMMEDIATE writes, revision-guarded saves,
/// an insert-only ledger written in the same transaction as every transition, a partial unique index allowing one active row per setting,
/// and fail-closed materialization. Nothing here knows registry paths; rows hold ids and typed snapshots only.
/// </summary>
public sealed partial class SqliteOperationStore
{
    private const string SettingActivePredicate = "quarantined=0 AND (state IN (1,2) OR compensation_prepared=1)";
    private static readonly JsonSerializerOptions SettingJournalOptions = new() { RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private const string SchemaV3 = """
        CREATE TABLE setting_operations(
            id TEXT PRIMARY KEY NOT NULL,
            payload TEXT NOT NULL,
            rev INTEGER NOT NULL CHECK(rev>=0),
            state INTEGER NOT NULL,
            compensation_prepared INTEGER NOT NULL CHECK(compensation_prepared IN (0,1)),
            setting_id TEXT NOT NULL,
            quarantined INTEGER NOT NULL DEFAULT 0 CHECK(quarantined IN (0,1)),
            quarantine_code TEXT,
            created_at_utc TEXT NOT NULL);
        CREATE UNIQUE INDEX ux_setting_operations_single_active ON setting_operations(setting_id) WHERE quarantined=0 AND (state IN (1,2) OR compensation_prepared=1);
        CREATE TABLE setting_events(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            op_id TEXT NOT NULL,
            op_rev INTEGER NOT NULL,
            from_state INTEGER,
            from_comp INTEGER,
            to_state INTEGER NOT NULL,
            to_comp INTEGER NOT NULL,
            code TEXT NOT NULL,
            at_utc TEXT NOT NULL,
            UNIQUE(op_id, op_rev));
        CREATE TRIGGER setting_events_no_update BEFORE UPDATE ON setting_events BEGIN SELECT RAISE(ABORT,'setting_events.immutable'); END;
        CREATE TRIGGER setting_events_no_delete BEFORE DELETE ON setting_events BEGIN SELECT RAISE(ABORT,'setting_events.immutable'); END;
        """;

    // v2 -> v3 runs inside the constructor's BEGIN IMMEDIATE: any failure rolls back and leaves the v2 file untouched.
    private void MigrateV2ToV3(SqliteConnection connection)
    {
        var tables = Tables(connection);
        if (tables.Contains("setting_operations") || tables.Contains("setting_events")) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
        try
        {
            Execute(connection, SchemaV3);
            faultHook?.Invoke("migration3.after_tables");
            Execute(connection, "PRAGMA user_version=3;");
            faultHook?.Invoke("migration3.after_version");
        }
        catch (SqliteException e) when (e.SqliteErrorCode is not (5 or 6)) { throw new InvalidDataException(ErrorCode.SchemaCorrupt, e); }
    }

    private static void VerifyV3(SqliteConnection connection, HashSet<string> tables)
    {
        foreach (var required in new[] { "setting_operations", "setting_events" })
            if (!tables.Contains(required)) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
        var objects = Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ux_setting_operations_single_active','setting_events_no_update','setting_events_no_delete');"), CultureInfo.InvariantCulture);
        if (objects != 3) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
    }

    // ---- prepare and save -------------------------------------------------------------------

    public SettingOperationRecord PrepareSetting(SettingOperationRecord record) => Write("setting.prepare", connection =>
    {
        if (Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM setting_operations WHERE {SettingActivePredicate};"), CultureInfo.InvariantCulture) != 0 || ActiveMachineRows(connection) != 0)
            throw new OperationException(ErrorCode.RecoveryRequired);
        if (!OperationTransitions.IsAllowedInitial(record.State, record.CompensationPrepared)) throw new OperationException(ErrorCode.InvalidTransition);
        record = record with { Revision = 0 };
        try
        {
            Execute(connection, "INSERT INTO setting_operations(id,payload,rev,state,compensation_prepared,setting_id,quarantined,created_at_utc) VALUES($id,$payload,0,$state,$comp,$setting,0,$created);",
                ("$id", record.Id.ToString("D")), ("$payload", JsonSerializer.Serialize(record, SettingJournalOptions)), ("$state", (int)record.State),
                ("$comp", record.CompensationPrepared ? 1 : 0), ("$setting", record.SettingId), ("$created", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("setting_id", StringComparison.Ordinal))
        {
            throw new OperationException(ErrorCode.RecoveryRequired);
        }
        InsertSettingEvent(connection, record.Id.ToString("D"), 0, null, null, (int)record.State, record.CompensationPrepared ? 1 : 0, record.Code);
        return record;
    });

    public SettingOperationRecord SaveSetting(SettingOperationRecord operation) => Write("setting.save", connection =>
    {
        var id = operation.Id.ToString("D");
        using var select = Command(connection, "SELECT rev,state,compensation_prepared,payload,quarantined FROM setting_operations WHERE id=$id;", [("$id", id)]);
        long storedRevision; int fromState, fromComp; string payload; bool quarantined;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) throw new OperationException(ErrorCode.OperationMissing);
            storedRevision = reader.GetInt64(0); fromState = reader.GetInt32(1); fromComp = reader.GetInt32(2); payload = reader.GetString(3); quarantined = reader.GetInt32(4) == 1;
        }
        if (storedRevision != operation.Revision || quarantined) throw new OperationException(ErrorCode.RevisionConflict);
        if (!OperationTransitions.IsAllowed((OperationState)fromState, fromComp == 1, operation.State, operation.CompensationPrepared))
            throw new OperationException(ErrorCode.InvalidTransition);
        var (previous, fault) = MaterializeSetting(id, payload, storedRevision);
        if (previous is null) throw new InvalidDataException(fault);
        // Identity and intent are immutable once journaled; the before-state never changes.
        if (previous.SettingId != operation.SettingId || previous.DefinitionVersion != operation.DefinitionVersion || previous.TargetFingerprint != operation.TargetFingerprint ||
            previous.ChoiceId != operation.ChoiceId || previous.SchemaVersion != operation.SchemaVersion || previous.CreatedAtUtc != operation.CreatedAtUtc ||
            previous.ObservedRevision != operation.ObservedRevision || !SettingSnapshots.SameSnapshot(previous.Before, operation.Before) ||
            previous.NeedsExplorerRestart != operation.NeedsExplorerRestart || previous.NeedsSignOut != operation.NeedsSignOut ||
            previous.Group != operation.Group || previous.Label != operation.Label)
            throw new OperationException(ErrorCode.InvalidTransition);
        var next = operation with { Revision = storedRevision + 1 };
        // Global single-active rule also applies to rows that become active again (compensation prepared), not just to new rows.
        if (OperationTransitions.IsActive(next.State, next.CompensationPrepared) && !OperationTransitions.IsActive((OperationState)fromState, fromComp == 1) &&
            (Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM setting_operations WHERE {SettingActivePredicate} AND id<>$id;", ("$id", id)), CultureInfo.InvariantCulture) != 0 || ActiveMachineRows(connection) != 0))
            throw new OperationException(ErrorCode.RecoveryRequired);
        try
        {
            using var update = Command(connection, "UPDATE setting_operations SET payload=$payload, rev=rev+1, state=$state, compensation_prepared=$comp WHERE id=$id AND rev=$rev;",
                [("$payload", JsonSerializer.Serialize(next, SettingJournalOptions)), ("$state", (int)next.State), ("$comp", next.CompensationPrepared ? 1 : 0), ("$id", id), ("$rev", storedRevision)]);
            if (update.ExecuteNonQuery() != 1) throw new OperationException(ErrorCode.RevisionConflict);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("setting_id", StringComparison.Ordinal))
        {
            throw new OperationException(ErrorCode.RecoveryRequired);
        }
        InsertSettingEvent(connection, id, next.Revision, fromState, fromComp, (int)next.State, next.CompensationPrepared ? 1 : 0, next.Code);
        return next;
    });

    public void QuarantineSetting(Guid id, long expectedRevision, string code) => Write("setting.quarantine", connection =>
    {
        var key = id.ToString("D");
        using var select = Command(connection, "SELECT state,compensation_prepared FROM setting_operations WHERE id=$id AND rev=$rev AND quarantined=0;", [("$id", key), ("$rev", expectedRevision)]);
        int state, comp;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) throw new OperationException(ErrorCode.RevisionConflict);
            state = reader.GetInt32(0); comp = reader.GetInt32(1);
        }
        Execute(connection, "UPDATE setting_operations SET quarantined=1, quarantine_code=$code, rev=rev+1 WHERE id=$id AND rev=$rev;", ("$code", code), ("$id", key), ("$rev", expectedRevision));
        InsertSettingEvent(connection, key, expectedRevision + 1, state, comp, state, comp, "journal.quarantined:" + code);
        return true;
    });

    // ---- reads ------------------------------------------------------------------------------

    public SettingOperationRecord GetSetting(Guid id) => Read(connection =>
    {
        using var command = Command(connection, "SELECT payload,rev,state,compensation_prepared FROM setting_operations WHERE id=$id AND quarantined=0;", [("$id", id.ToString("D"))]);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new OperationException(ErrorCode.OperationMissing);
        var (record, fault) = MaterializeSetting(id.ToString("D"), reader.GetString(0), reader.GetInt64(1));
        if (record is not null && ((int)record.State != reader.GetInt32(2) || record.CompensationPrepared != (reader.GetInt32(3) == 1))) fault = ErrorCode.JournalInvalid;
        return fault is null ? record! : throw new InvalidDataException(fault);
    });

    public IReadOnlyList<SettingOperationRecord> ListSettingOperations() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,payload,rev,state,compensation_prepared FROM setting_operations WHERE quarantined=0 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var records = new List<SettingOperationRecord>();
        while (reader.Read())
        {
            var (record, _) = MaterializeSetting(reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
            if (record is not null && (int)record.State == reader.GetInt32(3) && record.CompensationPrepared == (reader.GetInt32(4) == 1)) records.Add(record);
        }
        return (IReadOnlyList<SettingOperationRecord>)records;
    });

    public IReadOnlyList<SettingJournalEntry> ListRecoverableSettingEntries() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,payload,rev,state,compensation_prepared FROM setting_operations WHERE quarantined=0 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var entries = new List<SettingJournalEntry>();
        while (reader.Read())
        {
            var id = reader.GetString(0); var revision = reader.GetInt64(2);
            var state = reader.GetInt32(3); var comp = reader.GetInt32(4) == 1;
            var (record, fault) = MaterializeSetting(id, reader.GetString(1), revision);
            if (record is not null && ((int)record.State != state || record.CompensationPrepared != comp)) (record, fault) = (null, ErrorCode.JournalInvalid);
            var active = state is 1 or 2 || comp;
            if (active || record is null) entries.Add(new(Guid.TryParse(id, out var guid) ? guid : Guid.Empty, revision, record, fault));
        }
        return (IReadOnlyList<SettingJournalEntry>)entries;
    });

    public IReadOnlyList<QuarantinedEntry> ListQuarantinedSettings() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,quarantine_code,rev FROM setting_operations WHERE quarantined=1 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var list = new List<QuarantinedEntry>();
        while (reader.Read()) list.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2)));
        return (IReadOnlyList<QuarantinedEntry>)list;
    });

    public IReadOnlyList<OperationEvent> ReadSettingLedger(Guid? operationId = null) => Read(connection =>
    {
        using var command = Command(connection, "SELECT seq,op_id,op_rev,from_state,from_comp,to_state,to_comp,code,at_utc FROM setting_events WHERE $id IS NULL OR op_id=$id ORDER BY seq;",
            [("$id", operationId?.ToString("D"))]);
        using var reader = command.ExecuteReader();
        var events = new List<OperationEvent>();
        while (reader.Read())
            events.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : (OperationState)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4) == 1,
                (OperationState)reader.GetInt32(5), reader.GetInt32(6) == 1, reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        return (IReadOnlyList<OperationEvent>)events;
    });

    private static void InsertSettingEvent(SqliteConnection connection, string id, long revision, int? fromState, int? fromComp, int toState, int toComp, string code) =>
        Execute(connection, "INSERT INTO setting_events(op_id,op_rev,from_state,from_comp,to_state,to_comp,code,at_utc) VALUES($id,$rev,$fs,$fc,$ts,$tc,$code,$at);",
            ("$id", id), ("$rev", revision), ("$fs", fromState), ("$fc", fromComp), ("$ts", toState), ("$tc", toComp), ("$code", code), ("$at", Now()));

    // Fail closed: anything that is not exactly a current-format record for this row is untrusted.
    private static (SettingOperationRecord? Record, string? Fault) MaterializeSetting(string id, string payload, long revision)
    {
        try
        {
            var record = JsonSerializer.Deserialize<SettingOperationRecord>(payload, SettingJournalOptions);
            if (record is null || record.Id.ToString("D") != id || !Enum.IsDefined(record.State) || record.Before is null) return (null, ErrorCode.JournalInvalid);
            if (record.SchemaVersion != SettingOperationRecord.CurrentSchemaVersion) return (null, ErrorCode.SchemaUnsupported);
            if (!Enum.IsDefined(record.Before.View) || record.Before.Kind is { } bk && !Enum.IsDefined(bk) ||
                record.After is { } after && (!Enum.IsDefined(after.View) || after.Kind is { } ak && !Enum.IsDefined(ak))) return (null, ErrorCode.JournalInvalid);
            return (record with { Revision = revision }, null);
        }
        catch (JsonException) { return (null, ErrorCode.JournalInvalid); }
    }
}
