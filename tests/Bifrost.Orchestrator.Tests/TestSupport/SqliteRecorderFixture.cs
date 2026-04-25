using Bifrost.Recorder.Infrastructure;
using Bifrost.Recorder.Storage;
using Bifrost.Time;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Orchestrator.Tests.TestSupport;

/// <summary>
/// File-backed SQLite fixture that boots the Phase 02-shipped recorder
/// schema (<c>Migrations/001_initial.sql</c>) on a temp DB so Phase 06
/// audit-log tests can insert via <see cref="SessionDatabase.InsertMcCommands"/>
/// and assert via direct <c>SELECT</c>. Mirrors the in-process recorder
/// layout used by <c>RecorderPersistenceTests</c> but lives under the
/// orchestrator test project per the plan's <c>files_modified</c> list.
/// </summary>
/// <remarks>
/// File-backed (not <c>:memory:</c>) so the recorder's WAL pragma sequence
/// runs without the special-case in-memory-mode quirks; the
/// <see cref="Dispose"/> method best-effort cleans up the DB file. The
/// fixture exposes <see cref="Database"/> for write-side calls and
/// <see cref="QueryMcCommands"/> as a typed read-side helper so tests
/// don't need a Dapper dependency.
/// </remarks>
public sealed class SqliteRecorderFixture : IDisposable
{
    private readonly string _dbPath;

    public SessionDatabase Database { get; }

    public SqliteRecorderFixture(string tempDir)
    {
        _dbPath = Path.Combine(tempDir, "recorder.db");

        Database = new SessionDatabase($"Data Source={_dbPath}");
        Database.InitializePragmas();

        // FakeClock fixed at a known epoch so the schema_version row's
        // applied_at_ns is deterministic if a future test asserts on it.
        var clock = new FakeClock(new DateTimeOffset(2026, 4, 25, 0, 0, 0, TimeSpan.Zero));
        var migrator = new SchemaMigrator(Database, clock, NullLogger<SchemaMigrator>.Instance);
        migrator.ApplyPending();
    }

    /// <summary>
    /// Read every row from <c>mc_commands</c> in insertion order. Returns a
    /// list of typed records so tests assert on field values rather than
    /// raw SQLite reader positions.
    /// </summary>
    public List<McCommandRow> QueryMcCommands()
    {
        using var cmd = Database.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, ts_ns, command, args_json, result_json, operator_hostname " +
            "FROM mc_commands ORDER BY id";

        var rows = new List<McCommandRow>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add(new McCommandRow(
                Id: rdr.GetInt64(0),
                TsNs: rdr.GetInt64(1),
                Command: rdr.GetString(2),
                ArgsJson: rdr.GetString(3),
                ResultJson: rdr.GetString(4),
                OperatorHostname: rdr.GetString(5)));
        }
        return rows;
    }

    public void Dispose()
    {
        Database.Dispose();
        // SQLitePCL keeps a process-wide handle pool; the file is fair game
        // to delete because the test fixture's Dispose runs after the test
        // method exits and we own the only open connection.
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* best-effort temp cleanup */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* best-effort temp cleanup */ }
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>
/// Typed projection of one <c>mc_commands</c> row. Used by audit-log tests
/// to assert on individual fields without leaking SQLite reader plumbing.
/// </summary>
public sealed record McCommandRow(
    long Id,
    long TsNs,
    string Command,
    string ArgsJson,
    string ResultJson,
    string OperatorHostname);
