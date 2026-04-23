using System.Reflection;
using Bifrost.Recorder.Storage;
using Bifrost.Time;
using Microsoft.Extensions.Logging;

namespace Bifrost.Recorder.Infrastructure;

/// <summary>
/// Embedded-resource migration runner. Reads every *.sql file embedded under
/// the Migrations folder, orders by the leading numeric prefix in the
/// resource name, skips versions already recorded in schema_version, and
/// applies each remaining migration inside its own transaction.
/// </summary>
/// <remarks>
/// The schema_version table is owned by this class: it is created via
/// CREATE TABLE IF NOT EXISTS on every ApplyPending call so a fresh DB
/// bootstraps without a manual step. Each successful migration inserts a
/// row recording the applied-at timestamp (nanoseconds) and a human-readable
/// description; re-running ApplyPending is a no-op when every discovered
/// migration is already present.
/// </remarks>
public sealed class SchemaMigrator(SessionDatabase db, IClock clock, ILogger<SchemaMigrator> logger)
{
    private static readonly Assembly _asm = typeof(SchemaMigrator).Assembly;

    public void ApplyPending()
    {
        db.Execute(
            "CREATE TABLE IF NOT EXISTS schema_version (" +
            "version INTEGER PRIMARY KEY, " +
            "applied_at_ns INTEGER NOT NULL, " +
            "description TEXT NOT NULL)");

        var applied = db.Query<int>("SELECT version FROM schema_version ORDER BY version").ToHashSet();

        var pending = _asm.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Name: n, Version: ExtractVersion(n)))
            .Where(x => x.Version > 0 && !applied.Contains(x.Version))
            .OrderBy(x => x.Version)
            .ToList();

        foreach (var (resource, version) in pending)
        {
            using var stream = _asm.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded migration resource not found: {resource}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            var description = DescriptionFor(version);

            logger.LogInformation("Applying migration {Version}: {Description}", version, description);

            using var tx = db.BeginTransaction();
            db.ExecuteBatch(sql);
            db.Execute(
                "INSERT INTO schema_version(version, applied_at_ns, description) VALUES(@v, @t, @d)",
                new { v = version, t = clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000, d = description });
            tx.Commit();
        }

        logger.LogInformation("Schema migration complete");
    }

    /// <summary>
    /// Extract the leading integer from the first dot-separated segment that
    /// begins with a digit, e.g. "Bifrost.Recorder.Migrations.001_initial.sql"
    /// yields 1. Returns 0 on no match; such resources are skipped.
    /// </summary>
    private static int ExtractVersion(string resourceName)
    {
        var seg = resourceName.Split('.').FirstOrDefault(s => s.Length > 0 && char.IsDigit(s[0])) ?? "0";
        var digits = new string(seg.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : 0;
    }

    private static string DescriptionFor(int version) => version switch
    {
        1 => "Initial BIFROST schema",
        _ => $"Migration {version}",
    };
}
