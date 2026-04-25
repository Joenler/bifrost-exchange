using System.Text;
using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Publishes <c>gateway.heartbeat</c> on the public exchange every
/// <c>Gateway:Heartbeat:CadenceSeconds</c>. Phase 06 D-19: orchestrator auto-pauses
/// the round on heartbeat loss with a ≤ 10 s tolerance. Default cadence is 5 s —
/// 2× headroom against the orchestrator's cliff so a single dropped publish does
/// not trip the auto-pause.
///
/// Owns its own <see cref="IChannel"/> from the shared <see cref="IConnection"/>
/// (Pitfall 6). The orchestrator side (Phase 06 RabbitMqGatewayHeartbeatSource)
/// declares + binds the consumer queue; this service only PUBLISHES.
/// <see cref="PeriodicTimer"/> is constructed against the injected
/// <see cref="TimeProvider"/> so unit tests can drive the loop deterministically
/// via <c>FakeTimeProvider</c>.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IConnection _connection;
    private readonly IClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cadence;
    private readonly ILogger<HeartbeatService> _log;
    private IChannel? _channel;

    public HeartbeatService(
        IConnection connection,
        IClock clock,
        TimeProvider timeProvider,
        IConfiguration configuration,
        ILogger<HeartbeatService> log)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(configuration);
        var seconds = configuration.GetValue("Gateway:Heartbeat:CadenceSeconds", 5);
        if (seconds <= 0) seconds = 5;
        _cadence = TimeSpan.FromSeconds(seconds);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        // Heartbeat publishes onto the existing public exchange; no queue declared on
        // this side. The orchestrator declares + binds its own consumer queue.

        using var timer = new PeriodicTimer(_cadence, _timeProvider);
        _log.LogInformation("HeartbeatService started — cadence {Cadence}", _cadence);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PublishHeartbeatAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown.
        }
    }

    private async Task PublishHeartbeatAsync(CancellationToken ct)
    {
        var heartbeat = new HeartbeatPayload(
            Host: Environment.MachineName,
            Pid: Environment.ProcessId,
            TimestampUtc: _clock.GetUtcNow());
        var envelope = new Envelope<object>(
            MessageType: MessageTypes.GatewayHeartbeat,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: null,
            ClientId: "bifrost-gateway",
            InstrumentId: null,
            Sequence: null,
            Payload: heartbeat);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
        var props = new BasicProperties { ContentType = "application/json" };
        try
        {
            await _channel!.BasicPublishAsync(
                GatewayTopology.HeartbeatExchange,
                GatewayTopology.HeartbeatRoutingKey,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Heartbeat publish failed");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            try
            {
                await _channel.CloseAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "HeartbeatService channel close failed");
            }
            _channel.Dispose();
            _channel = null;
        }
        await base.StopAsync(cancellationToken);
    }

    private sealed record HeartbeatPayload(string Host, int Pid, DateTimeOffset TimestampUtc);
}
