// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// Durable SQLite journal (schema v4: v2 simulation tables, setting_operations/setting_events and machine_operations/machine_events) plus the simulated resource. Every write path runs in one BEGIN IMMEDIATE transaction;
/// SQLITE_BUSY/LOCKED surface as <see cref="StoreException"/> with code store.busy. Nothing here touches Windows state.
/// </summary>
public sealed partial class SqliteOperationStore : IOperationStore, ISettingOperationStore
{
    public const int CurrentSchemaVersion = 4;
    private const string ActivePredicate = "quarantined=0 AND (state IN (1,2) OR compensation_prepared=1)";
    private readonly string connectionString;
    // Microsoft.Data.Sqlite retries SQLITE_BUSY per command for DefaultTimeout whole seconds (0 would mean forever), so this is the effective wait.
    private readonly int busyTimeoutSeconds;
    private readonly Action<string>? faultHook;
    private static readonly JsonSerializerOptions JournalOptions = new() { RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public SqliteOperationStore(string databasePath) : this(databasePath, 5, null) { }

    // faultHook is invoked at named durable boundaries inside transactions (before commit); tests use it to inject crashes.
    internal SqliteOperationStore(string databasePath, int busyTimeoutSeconds, Action<string>? faultHook)
    {
        this.busyTimeoutSeconds = busyTimeoutSeconds;
        this.faultHook = faultHook;
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false, DefaultTimeout = busyTimeoutSeconds }.ToString();
        BackupBeforeMigration(fullPath);
        Write("initialize", Initialize);
    }

    // ---- connection and transaction plumbing -------------------------------------------------

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, $"PRAGMA busy_timeout={busyTimeoutSeconds * 1000}; PRAGMA synchronous=FULL;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static Exception Map(SqliteException e) =>
        e.SqliteErrorCode is 5 or 6 ? new StoreException(ErrorCode.StoreBusy, e) : new StoreException(ErrorCode.StoreFailure, e);

    private T Read<T>(Func<SqliteConnection, T> body)
    {
        try { using var connection = Open(); return body(connection); }
        catch (SqliteException e) { throw Map(e); }
    }

    private T Write<T>(string name, Func<SqliteConnection, T> body)
    {
        try
        {
            using var connection = Open();
            Execute(connection, "BEGIN IMMEDIATE");
            try
            {
                var result = body(connection);
                faultHook?.Invoke(name + ".before_commit");
                Execute(connection, "COMMIT");
                return result;
            }
            catch
            {
                try { Execute(connection, "ROLLBACK"); } catch (SqliteException) { /* connection already rolled back */ }
                throw;
            }
        }
        catch (SqliteException e) { throw Map(e); }
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters); command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters); return command.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    // ---- schema creation, verification and v1 -> v2 migration --------------------------------

    private const string SchemaV2 = """
        CREATE TABLE operations(
            id TEXT PRIMARY KEY NOT NULL,
            payload TEXT NOT NULL,
            rev INTEGER NOT NULL CHECK(rev>=0),
            state INTEGER NOT NULL,
            compensation_prepared INTEGER NOT NULL CHECK(compensation_prepared IN (0,1)),
            resource_id TEXT NOT NULL,
            quarantined INTEGER NOT NULL DEFAULT 0 CHECK(quarantined IN (0,1)),
            quarantine_code TEXT,
            created_at_utc TEXT NOT NULL);
        CREATE UNIQUE INDEX ux_operations_single_active ON operations(resource_id) WHERE quarantined=0 AND (state IN (1,2) OR compensation_prepared=1);
        CREATE TABLE op_events(
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
        CREATE TRIGGER op_events_no_update BEFORE UPDATE ON op_events BEGIN SELECT RAISE(ABORT,'op_events.immutable'); END;
        CREATE TRIGGER op_events_no_delete BEFORE DELETE ON op_events BEGIN SELECT RAISE(ABORT,'op_events.immutable'); END;
        """;
    private const string PreferencesTable = "CREATE TABLE preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL);";

    private bool Initialize(SqliteConnection connection)
    {
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version is < 0 or > CurrentSchemaVersion) throw new InvalidDataException(ErrorCode.SchemaUnsupported);
        var tables = Tables(connection);
        switch (version)
        {
            case 0:
                if (tables.Count != 0) throw new InvalidDataException(ErrorCode.SchemaUnsupported);
                Execute(connection, SchemaV2 + PreferencesTable +
                    "CREATE TABLE simulated_resource(id INTEGER PRIMARY KEY CHECK(id=1), value INTEGER NOT NULL CHECK(value IN (0,1)), revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0));" +
                    "INSERT INTO simulated_resource VALUES(1,0,0); " + SchemaV3 + SchemaV4 + " PRAGMA user_version=4;");
                break;
            case 1:
                try { MigrateV1(connection, tables); }
                catch (SqliteException e) when (e.SqliteErrorCode is not (5 or 6)) { throw new InvalidDataException(ErrorCode.SchemaCorrupt, e); }
                MigrateV2ToV3(connection);
                MigrateV3ToV4(connection);
                break;
            case 2:
                VerifyV2(connection, tables);
                MigrateV2ToV3(connection);
                MigrateV3ToV4(connection);
                break;
            case 3:
                VerifyV2(connection, tables);
                VerifyV3(connection, tables);
                MigrateV3ToV4(connection);
                break;
            default:
                VerifyV2(connection, tables);
                VerifyV3(connection, tables);
                VerifyV4(connection, tables);
                break;
        }
        return true;
    }

    /// <summary>
    /// Before an existing journal of an older schema is migrated, a consistent copy is written next to it (trust.md 3.5): temp file, then an atomic move, never overwritten,
    /// one backup per source schema version (<c>&lt;name&gt;.v3.bak</c>). A failed backup stops the migration, so the old file is never changed without a copy.
    /// </summary>
    private void BackupBeforeMigration(string fullPath)
    {
        if (!File.Exists(fullPath)) return;
        int version;
        try
        {
            using var probe = Open();
            version = Convert.ToInt32(Scalar(probe, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        }
        catch (SqliteException e) { throw Map(e); }
        if (version is < 1 or >= CurrentSchemaVersion) return;
        var backup = Path.Combine(Path.GetDirectoryName(fullPath)!, Path.GetFileNameWithoutExtension(fullPath) + $".v{version}.bak");
        if (File.Exists(backup)) return;
        var temp = backup + ".tmp";
        try
        {
            File.Delete(temp);
            using (var source = Open())
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }
            faultHook?.Invoke("backup.before_move");
            File.Move(temp, backup, overwrite: false);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort */ }
            throw new StoreException(ErrorCode.StoreFailure, e);
        }
    }

    private static HashSet<string> Tables(SqliteConnection connection)
    {
        using var command = Command(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';", []);
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static void VerifyV2(SqliteConnection connection, HashSet<string> tables)
    {
        foreach (var required in new[] { "operations", "op_events", "simulated_resource", "preferences" })
            if (!tables.Contains(required)) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
        var objects = Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ux_operations_single_active','op_events_no_update','op_events_no_delete');"), CultureInfo.InvariantCulture);
        if (objects != 3 || Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM simulated_resource WHERE id=1;"), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException(ErrorCode.SchemaCorrupt);
    }

    // Runs inside the constructor's BEGIN IMMEDIATE: any failure rolls the whole migration back and the v1 file is left untouched.
    private void MigrateV1(SqliteConnection connection, HashSet<string> tables)
    {
        // A v1 journal is evidence. A missing operations/resource table is corruption: fail closed instead of inventing empty state.
        if (!tables.Contains("operations") || !tables.Contains("simulated_resource") || tables.Contains("operations_v1"))
            throw new InvalidDataException(ErrorCode.SchemaCorrupt);
        if (Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM simulated_resource WHERE id=1;"), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException(ErrorCode.SchemaCorrupt);
        var rows = new List<(string Id, string Payload)>();
        using (var command = Command(connection, "SELECT id,payload FROM operations ORDER BY rowid;", []))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        faultHook?.Invoke("migration.after_read");
        Execute(connection, "ALTER TABLE operations RENAME TO operations_v1;");
        faultHook?.Invoke("migration.after_rename");
        Execute(connection, SchemaV2);
        var activeSeen = false;
        foreach (var (id, payload) in rows)
        {
            var (record, fault) = Materialize(id, payload, 0);
            var state = record is null ? -1 : (int)record.State;
            var comp = record?.CompensationPrepared == true ? 1 : 0;
            var quarantined = fault is not null;
            if (!quarantined && OperationTransitions.IsActive(record!.State, record.CompensationPrepared))
            {
                if (activeSeen) throw new InvalidDataException(ErrorCode.MigrationConflict);
                activeSeen = true;
            }
            Execute(connection, "INSERT INTO operations(id,payload,rev,state,compensation_prepared,resource_id,quarantined,quarantine_code,created_at_utc) VALUES($id,$payload,0,$state,$comp,$resource,$quarantined,$code,$created);",
                ("$id", id), ("$payload", payload), ("$state", state), ("$comp", comp), ("$resource", record?.ResourceId ?? "unknown"),
                ("$quarantined", quarantined ? 1 : 0), ("$code", fault), ("$created", record?.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture) ?? Now()));
            InsertEvent(connection, id, 0, null, null, state, comp, fault is null ? "journal.migrated_v1" : "journal.quarantined_v1:" + fault);
            faultHook?.Invoke("migration.after_row");
        }
        Execute(connection, "ALTER TABLE simulated_resource ADD COLUMN revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0);");
        if (!tables.Contains("preferences")) Execute(connection, PreferencesTable);
        Execute(connection, "DROP TABLE operations_v1;");
        faultHook?.Invoke("migration.before_version");
        Execute(connection, "PRAGMA user_version=2;");
    }

    // ---- resource ---------------------------------------------------------------------------

    public bool ReadActual() => ReadResource().Value;

    public ResourceState ReadResource() => Read(connection => ReadResource(connection));

    private static ResourceState ReadResource(SqliteConnection connection)
    {
        using var command = Command(connection, "SELECT value,revision FROM simulated_resource WHERE id=1;", []);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("simulation.resource_missing");
        return new(reader.GetInt32(0) == 1, reader.GetInt64(1));
    }

    /// <summary>Test seam simulating an external actor writing the resource. Bumps the revision like any other write; not on the port.</summary>
    internal void WriteActual(bool value) => Write("external", connection =>
    {
        Execute(connection, "UPDATE simulated_resource SET value=$value, revision=revision+1 WHERE id=1;", ("$value", value ? 1 : 0));
        return true;
    });

    public long? TryCompareExchange(Guid operationId, long operationRevision, bool expectedValue, long expectedResourceRevision, bool newValue) =>
        Write("mutate", connection =>
        {
            var stored = Scalar(connection, "SELECT rev FROM operations WHERE id=$id;", ("$id", operationId.ToString("D")));
            if (stored is null) throw new OperationException(ErrorCode.OperationMissing);
            if (Convert.ToInt64(stored, CultureInfo.InvariantCulture) != operationRevision) throw new OperationException(ErrorCode.RevisionConflict);
            using var update = Command(connection, "UPDATE simulated_resource SET value=$new, revision=revision+1 WHERE id=1 AND value=$expected AND revision=$revision;",
                [("$new", newValue ? 1 : 0), ("$expected", expectedValue ? 1 : 0), ("$revision", expectedResourceRevision)]);
            if (update.ExecuteNonQuery() != 1) return (long?)null;
            return ReadResource(connection).Revision;
        });

    // ---- operations -------------------------------------------------------------------------

    public OperationRecord Prepare(PlanToken? expected, Func<ResourceState, OperationRecord> build) => Write("prepare", connection =>
    {
        if (Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM operations WHERE {ActivePredicate};"), CultureInfo.InvariantCulture) != 0)
            throw new OperationException(ErrorCode.RecoveryRequired);
        var state = ReadResource(connection);
        if (expected is not null && (expected.Current != state.Value || expected.Revision != state.Revision))
            throw new OperationException(ErrorCode.PlanStale);
        return Insert(connection, build(state));
    });

    /// <summary>Compatibility/test seam for pre-built records (Before is caller supplied). The engine uses the token-aware overload.</summary>
    public void Prepare(OperationRecord operation) => Prepare(null, state => operation with { SourceRevision = operation.SourceRevision ?? state.Revision });

    private static OperationRecord Insert(SqliteConnection connection, OperationRecord operation)
    {
        if (!OperationTransitions.IsAllowedInitial(operation.State, operation.CompensationPrepared))
            throw new OperationException(ErrorCode.InvalidTransition);
        operation = operation with { Revision = 0 };
        try
        {
            Execute(connection, "INSERT INTO operations(id,payload,rev,state,compensation_prepared,resource_id,quarantined,created_at_utc) VALUES($id,$payload,0,$state,$comp,$resource,0,$created);",
                ("$id", operation.Id.ToString("D")), ("$payload", JsonSerializer.Serialize(operation)), ("$state", (int)operation.State),
                ("$comp", operation.CompensationPrepared ? 1 : 0), ("$resource", operation.ResourceId), ("$created", operation.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("resource_id", StringComparison.Ordinal))
        {
            throw new OperationException(ErrorCode.RecoveryRequired);
        }
        InsertEvent(connection, operation.Id.ToString("D"), 0, null, null, (int)operation.State, operation.CompensationPrepared ? 1 : 0, operation.Code);
        return operation;
    }

    public OperationRecord Save(OperationRecord operation, ResourceState? expectedResource = null) => Write("save", connection =>
    {
        if (expectedResource is not null && ReadResource(connection) != expectedResource) throw new OperationException(ErrorCode.ResourceChanged);
        var id = operation.Id.ToString("D");
        using var select = Command(connection, "SELECT rev,state,compensation_prepared,payload,quarantined FROM operations WHERE id=$id;", [("$id", id)]);
        long storedRevision; int fromState, fromComp; string payload; bool quarantined;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) throw new OperationException(ErrorCode.OperationMissing);
            storedRevision = reader.GetInt64(0); fromState = reader.GetInt32(1); fromComp = reader.GetInt32(2); payload = reader.GetString(3); quarantined = reader.GetInt32(4) == 1;
        }
        if (storedRevision != operation.Revision || quarantined) throw new OperationException(ErrorCode.RevisionConflict);
        if (!OperationTransitions.IsAllowed((OperationState)fromState, fromComp == 1, operation.State, operation.CompensationPrepared))
            throw new OperationException(ErrorCode.InvalidTransition);
        var (previous, fault) = Materialize(id, payload, storedRevision);
        if (previous is null) throw new InvalidDataException(fault);
        // Identity and intent are immutable once journaled.
        if (previous.FeatureId != operation.FeatureId || previous.ResourceId != operation.ResourceId || previous.Before != operation.Before ||
            previous.Desired != operation.Desired || previous.SourceRevision != operation.SourceRevision || previous.DefinitionVersion != operation.DefinitionVersion ||
            previous.SchemaVersion != operation.SchemaVersion || previous.CreatedAtUtc != operation.CreatedAtUtc)
            throw new OperationException(ErrorCode.InvalidTransition);
        var next = operation with { Revision = storedRevision + 1 };
        try
        {
            using var update = Command(connection, "UPDATE operations SET payload=$payload, rev=rev+1, state=$state, compensation_prepared=$comp WHERE id=$id AND rev=$rev;",
                [("$payload", JsonSerializer.Serialize(next)), ("$state", (int)next.State), ("$comp", next.CompensationPrepared ? 1 : 0), ("$id", id), ("$rev", storedRevision)]);
            if (update.ExecuteNonQuery() != 1) throw new OperationException(ErrorCode.RevisionConflict);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("resource_id", StringComparison.Ordinal))
        {
            throw new OperationException(ErrorCode.RecoveryRequired);
        }
        InsertEvent(connection, id, next.Revision, fromState, fromComp, (int)next.State, next.CompensationPrepared ? 1 : 0, next.Code);
        return next;
    });

    public void Quarantine(Guid id, long expectedRevision, string code) => Write("quarantine", connection =>
    {
        var key = id.ToString("D");
        using var select = Command(connection, "SELECT state,compensation_prepared FROM operations WHERE id=$id AND rev=$rev AND quarantined=0;", [("$id", key), ("$rev", expectedRevision)]);
        int state, comp;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) throw new OperationException(ErrorCode.RevisionConflict);
            state = reader.GetInt32(0); comp = reader.GetInt32(1);
        }
        Execute(connection, "UPDATE operations SET quarantined=1, quarantine_code=$code, rev=rev+1 WHERE id=$id AND rev=$rev;", ("$code", code), ("$id", key), ("$rev", expectedRevision));
        InsertEvent(connection, key, expectedRevision + 1, state, comp, state, comp, "journal.quarantined:" + code);
        return true;
    });

    public IReadOnlyList<QuarantinedEntry> ListQuarantined() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,quarantine_code,rev FROM operations WHERE quarantined=1 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var list = new List<QuarantinedEntry>();
        while (reader.Read()) list.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2)));
        return (IReadOnlyList<QuarantinedEntry>)list;
    });

    public OperationRecord Get(Guid id) => Read(connection =>
    {
        using var command = Command(connection, "SELECT payload,rev,state,compensation_prepared FROM operations WHERE id=$id AND quarantined=0;", [("$id", id.ToString("D"))]);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new OperationException(ErrorCode.OperationMissing);
        return Trusted(id.ToString("D"), reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3));
    });

    public IReadOnlyList<OperationRecord> ListOperations() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,payload,rev,state,compensation_prepared FROM operations WHERE quarantined=0 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var records = new List<OperationRecord>();
        while (reader.Read()) records.Add(Trusted(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4)));
        return (IReadOnlyList<OperationRecord>)records;
    });

    public IReadOnlyList<JournalEntry> ListRecoverableEntries() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,payload,rev,state,compensation_prepared FROM operations WHERE quarantined=0 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var entries = new List<JournalEntry>();
        while (reader.Read())
        {
            var id = reader.GetString(0); var revision = reader.GetInt64(2);
            var state = reader.GetInt32(3); var comp = reader.GetInt32(4) == 1;
            var (record, fault) = Materialize(id, reader.GetString(1), revision);
            if (record is not null && ((int)record.State != state || record.CompensationPrepared != comp)) (record, fault) = (null, ErrorCode.JournalInvalid);
            var active = state is 1 or 2 || comp;
            if (active || record is null) entries.Add(new(Guid.TryParse(id, out var guid) ? guid : Guid.Empty, revision, record, fault));
        }
        return (IReadOnlyList<JournalEntry>)entries;
    });

    public IReadOnlyList<OperationEvent> ReadLedger(Guid? operationId = null) => Read(connection =>
    {
        using var command = Command(connection, "SELECT seq,op_id,op_rev,from_state,from_comp,to_state,to_comp,code,at_utc FROM op_events WHERE $id IS NULL OR op_id=$id ORDER BY seq;",
            [("$id", operationId?.ToString("D"))]);
        using var reader = command.ExecuteReader();
        var events = new List<OperationEvent>();
        while (reader.Read())
            events.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : (OperationState)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4) == 1,
                (OperationState)reader.GetInt32(5), reader.GetInt32(6) == 1, reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        return (IReadOnlyList<OperationEvent>)events;
    });

    private static void InsertEvent(SqliteConnection connection, string id, long revision, int? fromState, int? fromComp, int toState, int toComp, string code) =>
        Execute(connection, "INSERT INTO op_events(op_id,op_rev,from_state,from_comp,to_state,to_comp,code,at_utc) VALUES($id,$rev,$fs,$fc,$ts,$tc,$code,$at);",
            ("$id", id), ("$rev", revision), ("$fs", fromState), ("$fc", fromComp), ("$ts", toState), ("$tc", toComp), ("$code", code), ("$at", Now()));

    private static OperationRecord Trusted(string id, string payload, long revision, int state, int comp)
    {
        var (record, fault) = Materialize(id, payload, revision);
        if (record is not null && ((int)record.State != state || record.CompensationPrepared != (comp == 1))) fault = ErrorCode.JournalInvalid;
        return fault is null ? record! : throw new InvalidDataException(fault);
    }

    // Fail closed: anything that is not exactly a v1-format record for this row is untrusted.
    private static (OperationRecord? Record, string? Fault) Materialize(string id, string payload, long revision)
    {
        try
        {
            var record = JsonSerializer.Deserialize<OperationRecord>(payload, JournalOptions);
            if (record is null || record.Id.ToString("D") != id || !Enum.IsDefined(record.State)) return (null, ErrorCode.JournalInvalid);
            if (record.SchemaVersion != 1) return (null, ErrorCode.SchemaUnsupported);
            return (record with { Revision = revision }, null);
        }
        catch (JsonException) { return (null, ErrorCode.JournalInvalid); }
    }

    // ---- preferences (application-owned, not journal data) -----------------------------------

    public string? ReadPreference(string key) => Read(connection => Scalar(connection, "SELECT value FROM preferences WHERE key=$key;", ("$key", key)) as string);

    public void WritePreference(string key, string value) => Write("preference", connection =>
    {
        Execute(connection, "INSERT INTO preferences(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;", ("$key", key), ("$value", value));
        return true;
    });
}
