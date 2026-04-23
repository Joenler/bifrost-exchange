using Bifrost.Recorder.Infrastructure;
using Bifrost.Recorder.Storage;
using Bifrost.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Recorder.Tests;

/// <summary>
/// REC-02 coverage: the embedded-resource migration runner is idempotent,
/// applies the full BIFROST v1 schema on first run, and stamps the
/// schema_version table with a human-readable description row.
/// </summary>
public sealed class SchemaMigratorTests
{
    private sealed class TestClock : IClock
    {
        private readonly FakeTimeProvider _provider = new();
        public DateTimeOffset GetUtcNow() => _provider.GetUtcNow();
    }

    [Fact]
    public void ApplyPending_CreatesAllNineTables_OnFirstRun()
    {
        using var db = new SessionDatabase("Data Source=:memory:");
        db.InitializePragmas();

        var migrator = new SchemaMigrator(db, new TestClock(), NullLogger<SchemaMigrator>.Instance);
        migrator.ApplyPending();

        var tables = db.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        Assert.Contains("book_updates", tables);
        Assert.Contains("trades", tables);
        Assert.Contains("orders", tables);
        Assert.Contains("fills", tables);
        Assert.Contains("rejects", tables);
        Assert.Contains("events", tables);
        Assert.Contains("mc_commands", tables);
        Assert.Contains("scorecards", tables);
        Assert.Contains("schema_version", tables);
    }

    [Fact]
    public void ApplyPending_IsIdempotent_ASecondCallIsANoOp()
    {
        using var db = new SessionDatabase("Data Source=:memory:");
        db.InitializePragmas();

        var migrator = new SchemaMigrator(db, new TestClock(), NullLogger<SchemaMigrator>.Instance);
        migrator.ApplyPending();
        migrator.ApplyPending();

        var versions = db.Query<int>("SELECT version FROM schema_version ORDER BY version").ToList();

        Assert.Single(versions);
        Assert.Equal(1, versions[0]);
    }

    [Fact]
    public void ApplyPending_RecordsDescriptionRow_OnFirstRun()
    {
        using var db = new SessionDatabase("Data Source=:memory:");
        db.InitializePragmas();

        var migrator = new SchemaMigrator(db, new TestClock(), NullLogger<SchemaMigrator>.Instance);
        migrator.ApplyPending();

        var description = db.Query<string>(
            "SELECT description FROM schema_version WHERE version = 1").Single();

        Assert.Equal("Initial BIFROST schema", description);
    }
}
