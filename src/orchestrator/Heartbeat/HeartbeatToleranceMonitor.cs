using System.Threading.Channels;
using Bifrost.Orchestrator.Actor;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.Orchestrator.Heartbeat;

/// <summary>
/// IClock-polling <see cref="BackgroundService"/> that marshals every
/// heartbeat-health transition through the orchestrator actor channel as a
/// <c>HeartbeatChangeMessage</c>. Subscribes to
/// <see cref="IGatewayHeartbeatSource.OnChange"/> in the constructor and
/// also polls <see cref="IGatewayHeartbeatSource.IsHealthy"/> every wall-second
/// — sources that don't raise events still surface transitions through the
/// poll path.
/// </summary>
/// <remarks>
/// Per Phase 06 SPEC Req 13 (no auto-advance): this monitor is one of the
/// two whitelisted <c>Task.Delay(</c> occurrences in the orchestrator source
/// tree (the other is <c>IterationSeedTimer</c>). It does NOT change
/// <c>RoundStateMachine.Current</c> — the actor's
/// <c>HandleHeartbeatChangeAsync</c> only sets the <c>Blocked</c>+<c>Paused</c>
/// flags on heartbeat-loss (per SPEC Req 11).
///
/// Per Phase 06 PATTERNS §F (BackgroundService shutdown hygiene): the source
/// is unsubscribed in <see cref="StopAsync"/> and the
/// <see cref="RabbitMqGatewayHeartbeatSource"/> (when active) drains its
/// channel via <c>IAsyncDisposable</c>.
///
/// Heartbeat-restored events are still enqueued onto the actor channel — the
/// actor's <c>HandleHeartbeatChangeAsync</c> logs the restore but does NOT
/// auto-clear <c>Blocked</c> (SPEC Req 11; only MC <c>Resume</c> clears the
/// block).
/// </remarks>
public sealed class HeartbeatToleranceMonitor : BackgroundService
{
    private readonly IGatewayHeartbeatSource _source;
    private readonly ChannelWriter<IOrchestratorMessage> _writer;
    private readonly IClock _clock;
    private readonly ILogger<HeartbeatToleranceMonitor> _logger;
    private bool _lastObservedHealthy = true;

    public HeartbeatToleranceMonitor(
        IGatewayHeartbeatSource source,
        ChannelWriter<IOrchestratorMessage> writer,
        IClock clock,
        ILogger<HeartbeatToleranceMonitor> logger)
    {
        _source = source;
        _writer = writer;
        _clock = clock;
        _logger = logger;

        _source.OnChange += OnSourceChange;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // RabbitMQ-backed source declares queue + binds during InitializeAsync;
        // the AlwaysHealthy default has no infrastructure to set up.
        if (_source is RabbitMqGatewayHeartbeatSource rabbitSource)
        {
            try
            {
                await rabbitSource.InitializeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to initialise RabbitMQ heartbeat consumer; monitor exiting");
                return;
            }
        }

        // Seed the initial observed-healthy state from the source. A boot-
        // healthy source matches the default; a boot-unhealthy source (e.g.
        // RabbitMqGatewayHeartbeatSource before the first heartbeat arrives)
        // surfaces an immediate HeartbeatChangeMessage so the actor picks up
        // the Blocked flag before any MC commands land.
        bool initialHealthy = _source.IsHealthy;
        if (!initialHealthy)
        {
            _lastObservedHealthy = true;  // force a transition observation
        }

        _logger.LogInformation(
            "HeartbeatToleranceMonitor started (initial healthy={Healthy})",
            initialHealthy);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Drain any pending heartbeats off the RabbitMQ queue (no-op for
            // AlwaysHealthy / manual-test sources).
            if (_source is RabbitMqGatewayHeartbeatSource pollSource)
            {
                await pollSource.DrainPendingHeartbeatsAsync(stoppingToken);
            }

            bool nowHealthy = _source.IsHealthy;
            if (nowHealthy == _lastObservedHealthy)
            {
                continue;
            }

            _lastObservedHealthy = nowHealthy;

            // RabbitMQ-backed source: surface the transition through its own
            // OnChange path so any other subscriber (currently only this
            // monitor) gets the same view as our enqueued message.
            if (_source is RabbitMqGatewayHeartbeatSource rabbit)
            {
                if (nowHealthy)
                {
                    rabbit.MarkHealthy();
                }
                else
                {
                    rabbit.MarkUnhealthy();
                }

                // MarkHealthy/MarkUnhealthy already raised OnChange which
                // routed through OnSourceChange → EnqueueAsync, so don't
                // double-enqueue here.
                continue;
            }

            // Sources that don't raise OnChange (e.g. test doubles that only
            // flip IsHealthy) need an explicit enqueue path.
            await EnqueueAsync(nowHealthy, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _source.OnChange -= OnSourceChange;
        await base.StopAsync(cancellationToken);

        if (_source is RabbitMqGatewayHeartbeatSource rabbit)
        {
            await rabbit.DisposeAsync();
        }
    }

    private void OnSourceChange(object? sender, GatewayHeartbeatChanged args)
    {
        // Fire-and-forget against CancellationToken.None: the actor channel
        // is bounded with FullMode=Wait, so a stuck WriteAsync would block
        // the source's invocation thread. The drain loop is fast enough at
        // 256-capacity that this is effectively non-blocking in practice;
        // a structured exception logger keeps a misbehaving source from
        // taking down the monitor.
        _ = EnqueueAsync(args.Healthy, CancellationToken.None);
    }

    private async Task EnqueueAsync(bool healthy, CancellationToken ct)
    {
        try
        {
            long tsNs = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await _writer.WriteAsync(new HeartbeatChangeMessage(tsNs, healthy), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // host shutdown
        }
        catch (ChannelClosedException)
        {
            // Channel completed by the host on shutdown — exit cleanly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue HeartbeatChangeMessage(healthy={Healthy})", healthy);
        }
    }
}
