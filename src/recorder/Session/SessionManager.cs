using System.Text.Json;
using Bifrost.Time;
using Microsoft.Extensions.Logging;

namespace Bifrost.Recorder.Session;

/// <summary>
/// Owns per-run session directories and atomic manifest writes. Ported from
/// Arena with the clock rewire: <see cref="GenerateRunId"/> is now an instance
/// method that pulls its timestamp from <see cref="IClock"/> so replay and
/// test fixtures can drive a deterministic clock.
/// </summary>
public sealed class SessionManager
{
    private readonly IClock _clock;
    private readonly ILogger<SessionManager> _logger;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public SessionManager(IClock clock, ILogger<SessionManager> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Build a sortable run-id of shape <c>yyyyMMdd-HHmmss-xxxxxx</c> from the
    /// injected clock plus a 6-char GUID suffix. Instance method (not static)
    /// so tests with a fake clock get deterministic output.
    /// </summary>
    public string GenerateRunId()
    {
        var timestamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return $"{timestamp}-{suffix}";
    }

    public string CreateSessionDirectory(string sessionsRoot, string runId)
    {
        var path = Path.Combine(sessionsRoot, runId);
        Directory.CreateDirectory(path);
        _logger.LogInformation("Created session directory: {Path}", path);
        return path;
    }

    /// <summary>
    /// Atomic tmp+rename manifest write. A partial file never appears on disk:
    /// the replace is a single rename syscall which is atomic on POSIX and
    /// replace-on-overwrite on Windows.
    /// </summary>
    public void WriteManifest(string sessionDir, Manifest manifest)
    {
        var targetPath = Path.Combine(sessionDir, "manifest.json");
        var tmpPath = targetPath + ".tmp";

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    public static string GetDbPath(string sessionDir) =>
        Path.Combine(sessionDir, "session.db");
}
