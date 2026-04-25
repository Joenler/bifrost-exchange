using Prometheus;

namespace Bifrost.Gateway.Metrics;

/// <summary>
/// Prometheus metric declarations per SPEC req 12 (GW-10 acceptance) and CONTEXT
/// D-12 / D-13 / D-14:
///   - D-12: prometheus-net.AspNetCore is the metrics library; OpenTelemetry stays
///     out (Phase 02 D-02). The static <c>Prometheus.Metrics.*</c> factory routes
///     through the default <c>CollectorRegistry</c> which the <c>app.MapMetrics()</c>
///     middleware in Program.cs serves at <c>/metrics</c>.
///   - D-13: histograms use the prometheus-net default buckets
///     (.005, .01, .025, .05, .1, .25, .5, 1, 2.5, 5, 10) — the .025/.05 boundary
///     brackets the 50ms p99 SLO; custom buckets are intentionally rejected.
///   - D-14: cardinality is uncapped. 8 teams × 8 guards × ~10 families ≈ 500 series
///     at event scale — trivial.
///
/// Anti-pattern reference (RESEARCH lines 446-457): do NOT add <c>team_name</c> to
/// per-instrument-per-fill HIGH-VOLUME counters that would explode cardinality.
/// The labels here are deliberate: <c>team_name</c> on the team-scoped counters
/// + <c>guard</c> on the guard-rejection counter only. <c>StructuralRejects</c>
/// is unlabelled because pre-Register frames have no resolvable team yet.
///
/// Naming: every family is prefixed <c>bifrost_gateway_</c> per the standard
/// Prometheus convention "component prefix + measurement + suffix"
/// (<c>_total</c> / <c>_seconds</c>).
/// </summary>
public static class GatewayMetrics
{
    public static readonly Counter OrdersSubmitted = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_orders_submitted_total",
            "Team order submits accepted at gateway (post-guard-chain).",
            new CounterConfiguration { LabelNames = new[] { "team_name" } });

    public static readonly Counter OrdersCancelled = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_orders_cancelled_total",
            "Team order cancels accepted at gateway.",
            new CounterConfiguration { LabelNames = new[] { "team_name" } });

    public static readonly Counter OrdersReplaced = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_orders_replaced_total",
            "Team order replaces accepted at gateway.",
            new CounterConfiguration { LabelNames = new[] { "team_name" } });

    public static readonly Counter Fills = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_fills_total",
            "OrderExecutedEvent deliveries fanned out to teams.",
            new CounterConfiguration { LabelNames = new[] { "team_name" } });

    public static readonly Counter GuardRejects = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_guard_rejects_total",
            "Guard-chain rejections labeled by guard "
            + "(structural / state_gate / rate_limited / max_open_orders / "
            + "max_notional / max_position / self_trade / other).",
            new CounterConfiguration { LabelNames = new[] { "team_name", "guard" } });

    public static readonly Counter StructuralRejects = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_structural_rejects_total",
            "Pre-Register structural rejections (no team_name resolvable yet).",
            new CounterConfiguration());

    public static readonly Counter Reconnects = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_reconnects_total",
            "Successful Register handshakes (initial + reconnect).",
            new CounterConfiguration { LabelNames = new[] { "team_name" } });

    public static readonly Histogram StreamLatency = Prometheus.Metrics
        .CreateHistogram("bifrost_gateway_stream_latency_seconds",
            "Inbound command handle latency from receive to ack-or-reject.",
            new HistogramConfiguration { LabelNames = new[] { "team_name" } });

    public static readonly Gauge RingBufferOccupancy = Prometheus.Metrics
        .CreateGauge("bifrost_gateway_ring_buffer_occupancy",
            "Per-team ring buffer occupancy (head - tail).",
            new GaugeConfiguration { LabelNames = new[] { "team_name" } });

    public static readonly Counter ForecastsDispatched = Prometheus.Metrics
        .CreateCounter("bifrost_gateway_forecasts_dispatched_total",
            "ForecastUpdate envelopes dispatched per team via cohort jitter.",
            new CounterConfiguration { LabelNames = new[] { "team_name" } });
}
