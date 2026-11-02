// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Winora.Application;
using Winora.Domain;
namespace Winora.Infrastructure.Persistence;

/// <summary>
/// Machine-operation journal. Same invariants as the setting journal: BEGIN IMMEDIATE writes, revision-guarded
/// saves, an insert-only ledger written in the same transaction as every transition, one active row per definition by index, ONE active row of any kind
/// across the user and machine tables, and fail-closed materialization. Rows hold ids and canonical snapshots only; the store knows no registry path.
/// </summary>
public sealed partial class SqliteOperationStore : IMachineOperationStore
{
    private const string MachineActivePredicate = "quarantined=0 AND (state IN (1,2) OR compensation_prepared=1)";
    private static readonly JsonSerializerOptions MachineJournalOptions = new() { RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private const string SchemaV4 = """
        CREATE TABLE machine_operations(
            id TEXT PRIMARY KEY NOT NULL,
            payload TEXT NOT NULL,
            rev INTEGER NOT NULL CHECK(rev>=0),
            state INTEGER NOT NULL,
            compensation_prepared INTEGER NOT NULL CHECK(compensation_prepared IN (0,1)),
            compensation_committed INTEGER NOT NULL CHECK(compensation_committed IN (0,1) AND (compensation_prepared=1 OR compensation_committed=0)),
            definition_id TEXT NOT NULL,
            quarantined INTEGER NOT NULL DEFAULT 0 CHECK(quarantined IN (0,1)),
            quarantine_code TEXT,
            created_at_utc TEXT NOT NULL);
        CREATE UNIQUE INDEX ux_machine_operations_single_active ON machine_operations(definition_id) WHERE quarantined=0 AND (state IN (1,2) OR compensation_prepared=1);
        CREATE TABLE machine_events(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            op_id TEXT NOT NULL,
            op_rev INTEGER NOT NULL,
            from_state INTEGER,
            from_comp INTEGER,
            from_commit INTEGER,
            to_state INTEGER NOT NULL,
            to_comp INTEGER NOT NULL,
            to_commit INTEGER NOT NULL,
            code TEXT NOT NULL,
            at_utc TEXT NOT NULL,
            UNIQUE(op_id, op_rev));
        CREATE TRIGGER machine_events_no_update BEFORE UPDATE ON machine_events BEGIN SELECT RAISE(ABORT,'machine_events.immutable'); END;
        CREATE TRIGGER machine_events_no_delete BEFORE DELETE ON machine_events BEGIN SELECT RAISE(ABORT,'machine_events.immutable'); END;
        """;

    // v3 -> v4 runs inside the constructor's BEGIN IMMEDIATE: any failure rolls back and leaves the v3 file untouched.
    private void MigrateV3ToV4(SqliteConnection connection)
    {
        var tables = Tables(connection);
        if (tables.Contains("machine_operations") || tables.Contains("machine_events")) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
        try
        {
            Execute(connection, SchemaV4);
            faultHook?.Invoke("migration4.after_tables");
            Execute(connection, "PRAGMA user_version=4;");
            faultHook?.Invoke("migration4.after_version");
        }
        catch (SqliteException e) when (e.SqliteErrorCode is not (5 or 6)) { throw new InvalidDataException(ErrorCode.SchemaCorrupt, e); }
    }

    private static void VerifyV4(SqliteConnection connection, HashSet<string> tables)
    {
        foreach (var required in new[] { "machine_operations", "machine_events" })
            if (!tables.Contains(required)) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
        var objects = Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ux_machine_operations_single_active','machine_events_no_update','machine_events_no_delete');"), CultureInfo.InvariantCulture);
        if (objects != 3) throw new InvalidDataException(ErrorCode.SchemaCorrupt);
    }

    /// <summary>Active rows of the other table family, for the global single-active rule.</summary>
    private static int ActiveSettingRows(SqliteConnection connection) =>
        Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM setting_operations WHERE {SettingActivePredicate};"), CultureInfo.InvariantCulture);

    private static int ActiveMachineRows(SqliteConnection connection, string? excludingId = null) =>
        Convert.ToInt32(Scalar(connection, $"SELECT COUNT(*) FROM machine_operations WHERE {MachineActivePredicate} AND ($id IS NULL OR id<>$id);", ("$id", excludingId)), CultureInfo.InvariantCulture);

    // ---- prepare and save -------------------------------------------------------------------

    public MachineOperationRecord PrepareMachine(MachineOperationRecord record) => Write("machine.prepare", connection =>
    {
        if (ActiveMachineRows(connection) != 0 || ActiveSettingRows(connection) != 0) throw new OperationException(ErrorCode.RecoveryRequired);
        if (!MachineTransitions.IsAllowedInitial(record.State, record.CompensationPrepared, record.CompensationCommitted)) throw new OperationException(ErrorCode.InvalidTransition);
        if (!ValidShape(record)) throw new OperationException(ErrorCode.JournalInvalid);
        record = record with { Revision = 0 };
        try
        {
            Execute(connection, "INSERT INTO machine_operations(id,payload,rev,state,compensation_prepared,compensation_committed,definition_id,quarantined,created_at_utc) VALUES($id,$payload,0,$state,0,0,$definition,0,$created);",
                ("$id", record.Id.ToString("D")), ("$payload", JsonSerializer.Serialize(record, MachineJournalOptions)), ("$state", (int)record.State),
                ("$definition", record.DefinitionId), ("$created", record.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("definition_id", StringComparison.Ordinal))
        {
            throw new OperationException(ErrorCode.RecoveryRequired);
        }
        InsertMachineEvent(connection, record.Id.ToString("D"), 0, null, null, null, (int)record.State, false, false, record.Code);
        return record;
    });

    public MachineOperationRecord SaveMachine(MachineOperationRecord operation) => Write("machine.save", connection =>
    {
        var id = operation.Id.ToString("D");
        using var select = Command(connection, "SELECT rev,state,compensation_prepared,compensation_committed,payload,quarantined FROM machine_operations WHERE id=$id;", [("$id", id)]);
        long storedRevision; int fromState, fromPrepared, fromCommitted; string payload; bool quarantined;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) throw new OperationException(ErrorCode.OperationMissing);
            storedRevision = reader.GetInt64(0); fromState = reader.GetInt32(1); fromPrepared = reader.GetInt32(2); fromCommitted = reader.GetInt32(3);
            payload = reader.GetString(4); quarantined = reader.GetInt32(5) == 1;
        }
        if (storedRevision != operation.Revision || quarantined) throw new OperationException(ErrorCode.RevisionConflict);
        if (!MachineTransitions.IsAllowed((OperationState)fromState, fromPrepared == 1, fromCommitted == 1, operation.State, operation.CompensationPrepared, operation.CompensationCommitted))
            throw new OperationException(ErrorCode.InvalidTransition);
        if (!ValidShape(operation)) throw new OperationException(ErrorCode.JournalInvalid);
        var (previous, fault) = MaterializeMachine(id, payload, storedRevision);
        if (previous is null) throw new InvalidDataException(fault);
        // Identity, intent and the before snapshot are immutable once journaled.
        if (previous.DefinitionId != operation.DefinitionId || previous.DefinitionVersion != operation.DefinitionVersion || previous.SetSerial != operation.SetSerial ||
            previous.SetHash != operation.SetHash || previous.BatchDigest != operation.BatchDigest || previous.PlanToken != operation.PlanToken ||
            previous.ChoiceId != operation.ChoiceId || previous.Before != operation.Before || previous.BeforeRevision != operation.BeforeRevision ||
            previous.SchemaVersion != operation.SchemaVersion || previous.CreatedAtUtc != operation.CreatedAtUtc || previous.RequiresRestart != operation.RequiresRestart)
            throw new OperationException(ErrorCode.InvalidTransition);
        if (previous.WorkerBefore is not null && previous.WorkerBefore != operation.WorkerBefore) throw new OperationException(ErrorCode.InvalidTransition);
        var next = operation with { Revision = storedRevision + 1 };
        // A row that becomes active again (compensation prepared) must also respect the global single-active rule.
        if (MachineTransitions.IsActive(next.State, next.CompensationPrepared) && !MachineTransitions.IsActive((OperationState)fromState, fromPrepared == 1) &&
            (ActiveMachineRows(connection, id) != 0 || ActiveSettingRows(connection) != 0))
            throw new OperationException(ErrorCode.RecoveryRequired);
        try
        {
            using var update = Command(connection, "UPDATE machine_operations SET payload=$payload, rev=rev+1, state=$state, compensation_prepared=$prep, compensation_committed=$commit WHERE id=$id AND rev=$rev;",
                [("$payload", JsonSerializer.Serialize(next, MachineJournalOptions)), ("$state", (int)next.State), ("$prep", next.CompensationPrepared ? 1 : 0),
                 ("$commit", next.CompensationCommitted ? 1 : 0), ("$id", id), ("$rev", storedRevision)]);
            if (update.ExecuteNonQuery() != 1) throw new OperationException(ErrorCode.RevisionConflict);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19 && e.Message.Contains("definition_id", StringComparison.Ordinal))
        {
            throw new OperationException(ErrorCode.RecoveryRequired);
        }
        InsertMachineEvent(connection, id, next.Revision, fromState, fromPrepared, fromCommitted, (int)next.State, next.CompensationPrepared, next.CompensationCommitted, next.Code);
        return next;
    });

    public void QuarantineMachine(Guid id, long expectedRevision, string code) => Write("machine.quarantine", connection =>
    {
        var key = id.ToString("D");
        using var select = Command(connection, "SELECT state,compensation_prepared,compensation_committed FROM machine_operations WHERE id=$id AND rev=$rev AND quarantined=0;", [("$id", key), ("$rev", expectedRevision)]);
        int state, prepared, committed;
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read()) throw new OperationException(ErrorCode.RevisionConflict);
            state = reader.GetInt32(0); prepared = reader.GetInt32(1); committed = reader.GetInt32(2);
        }
        Execute(connection, "UPDATE machine_operations SET quarantined=1, quarantine_code=$code, rev=rev+1 WHERE id=$id AND rev=$rev;", ("$code", code), ("$id", key), ("$rev", expectedRevision));
        InsertMachineEvent(connection, key, expectedRevision + 1, state, prepared, committed, state, prepared == 1, committed == 1, "journal.quarantined:" + code);
        return true;
    });

    // ---- reads ------------------------------------------------------------------------------

    public MachineOperationRecord GetMachine(Guid id) => Read(connection =>
    {
        using var command = Command(connection, "SELECT payload,rev,state,compensation_prepared,compensation_committed FROM machine_operations WHERE id=$id AND quarantined=0;", [("$id", id.ToString("D"))]);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new OperationException(ErrorCode.OperationMissing);
        var (record, fault) = MaterializeMachine(id.ToString("D"), reader.GetString(0), reader.GetInt64(1));
        if (record is not null && !ColumnsMatch(record, reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4))) fault = ErrorCode.JournalInvalid;
        return fault is null ? record! : throw new InvalidDataException(fault);
    });

    public IReadOnlyList<MachineOperationRecord> ListMachineOperations() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,payload,rev,state,compensation_prepared,compensation_committed FROM machine_operations WHERE quarantined=0 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var records = new List<MachineOperationRecord>();
        while (reader.Read())
        {
            var (record, _) = MaterializeMachine(reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
            if (record is not null && ColumnsMatch(record, reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5))) records.Add(record);
        }
        return (IReadOnlyList<MachineOperationRecord>)records;
    });

    public IReadOnlyList<MachineJournalEntry> ListRecoverableMachineEntries() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,payload,rev,state,compensation_prepared,compensation_committed FROM machine_operations WHERE quarantined=0 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var entries = new List<MachineJournalEntry>();
        while (reader.Read())
        {
            var id = reader.GetString(0); var revision = reader.GetInt64(2);
            var state = reader.GetInt32(3); var prepared = reader.GetInt32(4); var committed = reader.GetInt32(5);
            var (record, fault) = MaterializeMachine(id, reader.GetString(1), revision);
            if (record is not null && !ColumnsMatch(record, state, prepared, committed)) (record, fault) = (null, ErrorCode.JournalInvalid);
            var active = state is 1 or 2 || prepared == 1;
            if (active || record is null) entries.Add(new(Guid.TryParse(id, out var guid) ? guid : Guid.Empty, revision, record, fault));
        }
        return (IReadOnlyList<MachineJournalEntry>)entries;
    });

    public IReadOnlyList<QuarantinedEntry> ListQuarantinedMachine() => Read(connection =>
    {
        using var command = Command(connection, "SELECT id,quarantine_code,rev FROM machine_operations WHERE quarantined=1 ORDER BY rowid;", []);
        using var reader = command.ExecuteReader();
        var list = new List<QuarantinedEntry>();
        while (reader.Read()) list.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2)));
        return (IReadOnlyList<QuarantinedEntry>)list;
    });

    public IReadOnlyList<MachineOperationEvent> ReadMachineLedger(Guid? operationId = null) => Read(connection =>
    {
        using var command = Command(connection, "SELECT seq,op_id,op_rev,from_state,from_comp,from_commit,to_state,to_comp,to_commit,code,at_utc FROM machine_events WHERE $id IS NULL OR op_id=$id ORDER BY seq;",
            [("$id", operationId?.ToString("D"))]);
        using var reader = command.ExecuteReader();
        var events = new List<MachineOperationEvent>();
        while (reader.Read())
            events.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : (OperationState)reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4) == 1, reader.IsDBNull(5) ? null : reader.GetInt32(5) == 1,
                (OperationState)reader.GetInt32(6), reader.GetInt32(7) == 1, reader.GetInt32(8) == 1, reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
        return (IReadOnlyList<MachineOperationEvent>)events;
    });

    private static void InsertMachineEvent(SqliteConnection connection, string id, long revision, int? fromState, int? fromPrepared, int? fromCommitted, int toState, bool toPrepared, bool toCommitted, string code) =>
        Execute(connection, "INSERT INTO machine_events(op_id,op_rev,from_state,from_comp,from_commit,to_state,to_comp,to_commit,code,at_utc) VALUES($id,$rev,$fs,$fp,$fc,$ts,$tp,$tc,$code,$at);",
            ("$id", id), ("$rev", revision), ("$fs", fromState), ("$fp", fromPrepared), ("$fc", fromCommitted), ("$ts", toState), ("$tp", toPrepared ? 1 : 0), ("$tc", toCommitted ? 1 : 0), ("$code", code), ("$at", Now()));

    private static bool ColumnsMatch(MachineOperationRecord record, int state, int prepared, int committed) =>
        (int)record.State == state && record.CompensationPrepared == (prepared == 1) && record.CompensationCommitted == (committed == 1);

    /// <summary>Shape rules every stored row must meet: digests are 64 hex characters, snapshots are canonical, flags are consistent.</summary>
    private static bool ValidShape(MachineOperationRecord r) =>
        Enum.IsDefined(r.State) && MachineTransitions.IsConsistent(r.CompensationPrepared, r.CompensationCommitted) &&
        r.DefinitionVersion >= 1 && r.SetSerial >= 0 && !string.IsNullOrEmpty(r.DefinitionId) && r.DefinitionId.Length <= 64 &&
        MachineHex.IsHexDigest(r.SetHash) && MachineHex.IsHexDigest(r.BatchDigest) && MachineHex.IsHexDigest(r.PlanToken) &&
        (r.RestorePlanToken is null || MachineHex.IsHexDigest(r.RestorePlanToken)) &&
        MachineHex.TryDecodeSnapshot(r.Before, out _) &&
        (r.WorkerBefore is null || MachineHex.TryDecodeSnapshot(r.WorkerBefore, out _)) &&
        (r.After is null || MachineHex.TryDecodeSnapshot(r.After, out _)) &&
        (r.Restore is null || MachineHex.TryDecodeSnapshot(r.Restore, out _)) &&
        r.BootSession.Length <= 64 && r.Code.Length is > 0 and <= 80;

    // Fail closed: anything that is not exactly a current-format record for this row is untrusted.
    private static (MachineOperationRecord? Record, string? Fault) MaterializeMachine(string id, string payload, long revision)
    {
        try
        {
            var record = JsonSerializer.Deserialize<MachineOperationRecord>(payload, MachineJournalOptions);
            if (record is null || record.Id.ToString("D") != id) return (null, ErrorCode.JournalInvalid);
            if (record.SchemaVersion != MachineOperationRecord.CurrentSchemaVersion) return (null, ErrorCode.SchemaUnsupported);
            if (!ValidShape(record)) return (null, ErrorCode.JournalInvalid);
            return (record with { Revision = revision }, null);
        }
        catch (JsonException) { return (null, ErrorCode.JournalInvalid); }
    }
}
