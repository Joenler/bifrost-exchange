using System.Text.Json;

namespace Bifrost.Recorder.Session;

/// <summary>
/// Append-only <c>index.json</c> sidecar at <c>sessionsRoot</c>. Lists every
/// completed run so the export tooling and dashboards can enumerate sessions
/// without walking per-run manifests. Writes follow the tmp+rename pattern.
/// </summary>
public sealed class SessionIndex
{
    private readonly string _indexPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public SessionIndex(string sessionsRoot)
    {
        _indexPath = Path.Combine(sessionsRoot, "index.json");
    }

    public List<SessionIndexEntry> ReadAll()
    {
        if (!File.Exists(_indexPath))
            return [];

        var json = File.ReadAllText(_indexPath);
        return JsonSerializer.Deserialize<List<SessionIndexEntry>>(json, JsonOptions) ?? [];
    }

    public void AddEntry(SessionIndexEntry entry)
    {
        var entries = ReadAll();
        entries.Add(entry);

        var tmpPath = _indexPath + ".tmp";
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, _indexPath, overwrite: true);
    }
}

public sealed class SessionIndexEntry
{
    public string RunId { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>
    /// Renamed from Arena's <c>StrategyCount</c> per BIFROST team semantics
    /// (D-14): participants are teams, not per-strategy processes.
    /// </summary>
    public int TeamCount { get; set; }

    public int InstrumentCount { get; set; }
}
