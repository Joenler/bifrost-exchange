namespace Bifrost.Orchestrator.Heartbeat;

/// <summary>
/// BIFROST-invented seam supplying the gateway-heartbeat health flag to the
/// orchestrator's <see cref="HeartbeatToleranceMonitor"/>. Two implementations
/// ship in Phase 06:
/// <list type="bullet">
///   <item><see cref="AlwaysHealthyGatewayHeartbeatSource"/> — the default
///         registration when <c>Orchestrator:Heartbeat:Enabled=false</c>; tests
///         + Phase 00-06 green-path bring-up rely on this default.</item>
///   <item><see cref="RabbitMqGatewayHeartbeatSource"/> — disabled-by-default
///         until Phase 07 flips <c>Orchestrator:Heartbeat:Enabled=true</c>;
///         binds the <c>bifrost.gateway.heartbeat</c> exchange and tracks the
///         last-seen heartbeat timestamp.</item>
/// </list>
/// Mirrors the Phase 02 <c>IRoundStateSource</c> seam shape verbatim per
/// CONTEXT D-18 — same <c>Current</c>+<c>OnChange</c> pair, same CS0067
/// suppression on the production static-value implementation.
/// </summary>
public interface IGatewayHeartbeatSource
{
    /// <summary>
    /// True while the gateway-heartbeat producer is observed-healthy. The
    /// orchestrator's <see cref="HeartbeatToleranceMonitor"/> polls this every
    /// wall-second and enqueues a <c>HeartbeatChangeMessage</c> on transition.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Raised when the underlying source detects a transition between
    /// healthy and unhealthy. The <see cref="HeartbeatToleranceMonitor"/>
    /// subscribes here in addition to its own polling so RabbitMQ-driven
    /// sources can fire change events synchronously.
    /// </summary>
    event EventHandler<GatewayHeartbeatChanged> OnChange;
}

/// <summary>
/// Payload fired by <see cref="IGatewayHeartbeatSource.OnChange"/> when the
/// gateway-heartbeat health flag transitions. <paramref name="TimestampNs"/>
/// is nanoseconds since Unix epoch — the same wall-clock projection the
/// orchestrator uses elsewhere (<c>IClock.GetUtcNow().ToUnixTimeMilliseconds()
/// * 1_000_000</c>).
/// </summary>
public sealed record GatewayHeartbeatChanged(bool Healthy, long TimestampNs);
