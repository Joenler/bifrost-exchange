namespace Bifrost.Orchestrator;

/// <summary>
/// Bound from the "Orchestrator" appsettings section. Field defaults match the
/// dev-shipped values; production overrides via environment variables or
/// docker-compose env-files. Consumers (downstream plans) read these values
/// from <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>.
/// </summary>
public sealed class OrchestratorOptions
{
    public string StatePath { get; set; } = "/tmp/bifrost-orchestrator-state.json";

    public long MasterSeed { get; set; }

    public int IterationSeedRotationSeconds { get; set; } = 300;

    public string NewsLibraryPath { get; set; } = "/app/config/news-library.json";

    public HeartbeatOptions Heartbeat { get; set; } = new();

    public sealed class HeartbeatOptions
    {
        public bool Enabled { get; set; }

        public int ToleranceSeconds { get; set; } = 10;
    }
}
