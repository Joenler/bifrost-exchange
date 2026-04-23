using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bifrost.Recorder.Storage;

/// <summary>
/// Minimal facade over a single SqliteConnection. The production port lands
/// later with the full batched write loop and prepared-command insert
/// patterns; this shim exposes exactly the surface SchemaMigrator needs and
/// keeps the public contract stable so the later replacement is
/// internals-only.
/// </summary>
public sealed class SessionDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SessionDatabase(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
    }

    public SqliteConnection Connection => _connection;

    /// <summary>
    /// Apply the Arena 5-pragma sequence for a WAL-mode recorder connection.
    /// Per-connection settings (WAL mode, synchronous, cache, mmap) live here
    /// rather than in the migration SQL because they are connection-level
    /// state, not schema definitions.
    /// </summary>
    public void InitializePragmas()
    {
        _connection.Execute("PRAGMA journal_mode = 'wal'");
        _connection.Execute("PRAGMA synchronous = NORMAL");
        _connection.Execute("PRAGMA wal_autocheckpoint = 10000");
        _connection.Execute("PRAGMA cache_size = -32768");
        _connection.Execute("PRAGMA mmap_size = 67108864");
    }

    public void Execute(string sql, object? param = null) => _connection.Execute(sql, param);

    public void ExecuteBatch(string sql) => _connection.Execute(sql);

    public IEnumerable<T> Query<T>(string sql, object? param = null) => _connection.Query<T>(sql, param);

    public IDbTransaction BeginTransaction() => _connection.BeginTransaction();

    public void Dispose() => _connection.Dispose();
}
