using Bifrost.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Bifrost.Orchestrator.Heartbeat;

/// <summary>
/// RabbitMQ-backed <see cref="IGatewayHeartbeatSource"/> that subscribes to
/// the <c>bifrost.gateway.heartbeat</c> topic exchange (routing key
/// <c>gateway.heartbeat</c>) and tracks the last-seen heartbeat timestamp.
/// Health is a snapshot-vs-tolerance check: <c>elapsed_ns &gt;
/// ToleranceSeconds * 1e9</c> means the producer is silent and the source
/// reports unhealthy.
/// </summary>
/// <remarks>
/// Disabled by default until Phase 07 flips
/// <c>Orchestrator:Heartbeat:Enabled=true</c>. Phase 06's compose smoke runs
/// with <see cref="AlwaysHealthyGatewayHeartbeatSource"/> so this consumer
/// never opens a queue against a non-existent producer exchange.
///
/// Per Phase 06 CONTEXT D-19 + PATTERNS §F (BackgroundService shutdown
/// hygiene): the consumer owns its own <see cref="IChannel"/> created from
/// the shared <see cref="IConnection"/>. RabbitMQ.Client 7.x channels are
/// not thread-safe; sharing the orchestrator publisher's channel for queue
/// declare + consume would race against the actor's publish path. The
/// poll-mode <c>BasicGetAsync</c> loop matches the
/// <c>McRegimeForceConsumer</c> template (see PATTERNS §F).
///
/// The <see cref="HeartbeatToleranceMonitor"/> drives the actual transition
/// detection: this source only updates <see cref="IsHealthy"/> + raises
/// <see cref="OnChange"/> when the monitor calls <see cref="MarkHealthy"/>
/// or <see cref="MarkUnhealthy"/>. Keeps the source single-purpose (last-seen
/// tracker) and the monitor responsible for poll-cadence + transition
/// arbitration.
/// </remarks>
public sealed class RabbitMqGatewayHeartbeatSource : IGatewayHeartbeatSource, IAsyncDisposable
{
    public const string HeartbeatExchange = "bifrost.gateway.heartbeat";
    public const string HeartbeatRoutingKey = "gateway.heartbeat";

    private readonly IConnection _connection;
    private readonly IClock _clock;
    private readonly ILogger<RabbitMqGatewayHeartbeatSource> _logger;
    private readonly int _toleranceSeconds;

    private IChannel? _channel;
    private string? _queueName;
    private long _lastHeartbeatNs;
    private bool _currentHealthy;

    public RabbitMqGatewayHeartbeatSource(
        IConnection connection,
        IClock clock,
        IOptions<OrchestratorOptions> opts,
        ILogger<RabbitMqGatewayHeartbeatSource> logger)
    {
        _connection = connection;
        _clock = clock;
        _logger = logger;
        _toleranceSeconds = Math.Max(1, opts.Value.Heartbeat.ToleranceSeconds);
    }

    /// <summary>
    /// True while the most recent heartbeat is within
    /// <c>Orchestrator:Heartbeat:ToleranceSeconds</c> of <c>IClock.GetUtcNow()</c>.
    /// Falsy before the first heartbeat arrives (a producer-not-started boot
    /// reports unhealthy until the first message lands, which matches the
    /// SPEC Req 11 fail-closed posture).
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            if (_lastHeartbeatNs == 0)
            {
                return false;
            }

            long nowNs = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            long elapsedNs = nowNs - _lastHeartbeatNs;
            long toleranceNs = (long)_toleranceSeconds * 1_000_000_000L;
            return elapsedNs <= toleranceNs;
        }
    }

    public event EventHandler<GatewayHeartbeatChanged>? OnChange;

    /// <summary>
    /// Declare exchange + transient queue + binding + start the poll loop.
    /// Called once from <see cref="HeartbeatToleranceMonitor.ExecuteAsync"/>
    /// when the source is registered (i.e.
    /// <c>Orchestrator:Heartbeat:Enabled=true</c>). Pulls the channel from
    /// the shared <see cref="IConnection"/> so the orchestrator publisher's
    /// channel is never shared across producers.
    /// </summary>
    public async Task InitializeAsync(CancellationToken stoppingToken)
    {
        _channel = await _connection.CreateChannelAsync(
            cancellationToken: stoppingToken);

        await _channel.ExchangeDeclareAsync(
            HeartbeatExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        // Per-instance ephemeral fanout receiver: each running orchestrator
        // declares its own queue, exclusive+autoDelete tied to the connection.
        // Matches the McRegimeForceConsumer shape exactly — RabbitMQ 4 hard-
        // blocks transient non-exclusive queues by default.
        _queueName = $"bifrost.orchestrator.heartbeat.{Guid.NewGuid():N}";
        await _channel.QueueDeclareAsync(
            _queueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken);

        await _channel.QueueBindAsync(
            _queueName,
            HeartbeatExchange,
            HeartbeatRoutingKey,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Gateway heartbeat consumer started on queue {Queue} (poll mode, tolerance={Sec}s)",
            _queueName,
            _toleranceSeconds);
    }

    /// <summary>
    /// Pulls one heartbeat message off the bound queue (if any) and updates
    /// <see cref="_lastHeartbeatNs"/>. Callers (the
    /// <see cref="HeartbeatToleranceMonitor"/>) invoke this every wall-second
    /// in their poll loop.
    /// </summary>
    public async Task DrainPendingHeartbeatsAsync(CancellationToken stoppingToken)
    {
        if (_channel is null || _queueName is null)
        {
            return;
        }

        try
        {
            // Drain everything pending so a backlog doesn't artificially keep
            // us looking healthy after the producer dies (the elapsed clock
            // is what governs IsHealthy, but draining keeps the queue clean).
            while (!stoppingToken.IsCancellationRequested)
            {
                BasicGetResult? result = await _channel.BasicGetAsync(
                    _queueName,
                    autoAck: true,
                    stoppingToken);

                if (result is null)
                {
                    break;
                }

                _lastHeartbeatNs = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            }
        }
        catch (OperationCanceledException)
        {
            // Caller is shutting down; nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat poll iteration failed; continuing");
        }
    }

    /// <summary>
    /// Called by <see cref="HeartbeatToleranceMonitor"/> after detecting a
    /// healthy transition (a heartbeat arrived after the source was
    /// previously unhealthy). Raises <see cref="OnChange"/> with
    /// <c>Healthy=true</c>; idempotent if already healthy.
    /// </summary>
    public void MarkHealthy()
    {
        if (_currentHealthy)
        {
            return;
        }

        _currentHealthy = true;
        long tsNs = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
        OnChange?.Invoke(this, new GatewayHeartbeatChanged(Healthy: true, TimestampNs: tsNs));
    }

    /// <summary>
    /// Called by <see cref="HeartbeatToleranceMonitor"/> after detecting an
    /// unhealthy transition (elapsed-since-last exceeded tolerance). Raises
    /// <see cref="OnChange"/> with <c>Healthy=false</c>; idempotent if
    /// already unhealthy.
    /// </summary>
    public void MarkUnhealthy()
    {
        if (!_currentHealthy)
        {
            return;
        }

        _currentHealthy = false;
        long tsNs = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
        OnChange?.Invoke(this, new GatewayHeartbeatChanged(Healthy: false, TimestampNs: tsNs));
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            try
            {
                await _channel.CloseAsync();
            }
            catch
            {
                // best-effort shutdown
            }

            _channel = null;
        }
    }
}
