namespace Bifrost.Recorder.Infrastructure;

/// <summary>
/// Plain in-memory counters exposed to <see cref="Bifrost.Recorder.Storage.WriteLoop"/>
/// and <see cref="RabbitMqRecorderConsumer"/>. Stripped of Arena's OpenTelemetry
/// metric surface: Phase 02 runs on LAN-only commodity hardware, no OTel collector
/// is deployed, and the in-memory values are read directly by log output and
/// the degraded-mode check.
/// </summary>
/// <remarks>
/// Mutable POCO by design: both the drain loop and the consumer share a single
/// instance and update fields in place. Access is single-writer per field
/// (WriteLoop writes ChannelDepth, EventsRecorded, LastBatchDurationMs,
/// BackpressureWarnings; the consumer writes IsDegraded, EventsDropped).
/// No scoring-relevant state lives here, so the lint-concurrent-dictionary
/// discipline does not apply.
/// </remarks>
public sealed class RecorderMetrics
{
    public bool IsDegraded { get; set; }

    public long ChannelDepth { get; set; }

    public long EventsRecorded { get; set; }

    public long EventsDropped { get; set; }

    public long BackpressureWarnings { get; set; }

    public double LastBatchDurationMs { get; set; }
}
