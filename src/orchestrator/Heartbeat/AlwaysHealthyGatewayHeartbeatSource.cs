namespace Bifrost.Orchestrator.Heartbeat;

/// <summary>
/// Default <see cref="IGatewayHeartbeatSource"/> implementation when
/// <c>Orchestrator:Heartbeat:Enabled=false</c>. Always reports healthy and
/// never raises <see cref="OnChange"/>. Used by every test that does not
/// drive the heartbeat-loss path (the existing 06-04 → 06-09 suites build
/// the actor without booting <see cref="HeartbeatToleranceMonitor"/>) and by
/// Phase 00-06 green-path bring-up before Phase 07 ships the gateway-side
/// heartbeat producer.
/// </summary>
/// <remarks>
/// CS0067 suppression: <see cref="OnChange"/> is part of the
/// <see cref="IGatewayHeartbeatSource"/> contract but is never raised in this
/// production static-value implementation — same convention as Phase 02's
/// <c>ConfigRoundStateSource</c>. The test-only manual heartbeat sources
/// + the future <see cref="RabbitMqGatewayHeartbeatSource"/> raise it on
/// transition.
/// </remarks>
public sealed class AlwaysHealthyGatewayHeartbeatSource : IGatewayHeartbeatSource
{
    public bool IsHealthy => true;

#pragma warning disable CS0067 // OnChange never raised — see remarks above
    public event EventHandler<GatewayHeartbeatChanged>? OnChange;
#pragma warning restore CS0067
}
