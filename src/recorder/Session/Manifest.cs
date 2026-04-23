namespace Bifrost.Recorder.Session;

/// <summary>
/// Per-run recorder manifest written to <c>manifest.json</c> at session-dir
/// root. Fields are BIFROST-native: participating teams + scenario seeds +
/// MC operator hostname + BIFROST version. Arena's <c>ManifestStrategy</c> is
/// renamed to <see cref="ManifestTeam"/> (drops the Status field; team
/// lifecycle is owned by the round orchestrator, not the recorder).
/// </summary>
public sealed class Manifest
{
    public string RunId { get; init; } = "";

    /// <summary>
    /// Alias for <see cref="RunId"/> matching the event-run terminology used
    /// throughout the spec. Populated to the same value by Program.cs.
    /// </summary>
    public string EventRunId { get; init; } = "";

    public string Name { get; init; } = "";
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; set; }
    public string? ExitReason { get; set; }

    public List<ManifestTeam> ParticipatingTeams { get; init; } = [];

    /// <summary>
    /// RNG seed per scenario component. Captured at round start so replays
    /// are byte-reproducible from the manifest alone.
    /// </summary>
    public long[] ScenarioSeeds { get; init; } = [];

    public string McOperatorHostname { get; init; } = "";
    public string BifrostVersion { get; init; } = "";

    public int InstrumentCount { get; set; }
    public ManifestConfig? ConfigSnapshot { get; set; }
    public ManifestEventCounts EventCounts { get; set; } = new();
}

public sealed class ManifestTeam
{
    public string Name { get; init; } = "";
    public string ClientId { get; init; } = "";
}

public sealed class ManifestConfig
{
    public string[] ExchangeAreas { get; init; } = [];
    public int TickSize { get; init; }
    public int PriceScale { get; init; }
}

/// <summary>
/// Row counts stamped at graceful shutdown. Shape mirrors the BIFROST split
/// tables (book_updates, trades, orders, fills, rejects, events); Arena's
/// 3-field shape (OrderEvents/LifecycleEvents/MetricsSnapshots) is replaced.
/// </summary>
public sealed class ManifestEventCounts
{
    public int BookUpdates { get; set; }
    public int Trades { get; set; }
    public int Orders { get; set; }
    public int Fills { get; set; }
    public int Rejects { get; set; }
    public int Events { get; set; }
}
