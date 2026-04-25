using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Translation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Round-state fan-out consumer. <see cref="AsyncEventingBasicConsumer"/>
/// push pattern (Pitfall 9 — NEVER <c>BasicGetAsync</c> poll). Owns its OWN
/// <see cref="IChannel"/> from the shared <see cref="IConnection"/> (Pitfall 6).
///
/// Binds <see cref="GatewayTopology.RoundExchange"/> with pattern
/// <c>round.state.#</c>. On every <see cref="MessageTypes.RoundStateChanged"/>
/// envelope: translate → broadcast to all teams.
///
/// Critically, on the Settled→IterationOpen transition (D-11), invokes
/// <see cref="TeamRegistry.OnSettledToIterationOpen"/> BEFORE the broadcast
/// so the wipe is observable to the inbound stream the moment teams resume.
///
/// Pitfall 10: ring-Append + lock release → outbound write.
/// </summary>
public sealed class RoundStateConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConnection _connection;
    private readonly TeamRegistry _registry;
    private readonly ILogger<RoundStateConsumer> _log;
    private IChannel? _channel;

    // The consumer is the SOLE mutator of this field (RabbitMQ.Client delivers
    // each message to a single consumer instance sequentially), so no
    // synchronization is required.
    private RoundProto.State _previousState = RoundProto.State.Unspecified;

    public RoundStateConsumer(
        IConnection connection,
        TeamRegistry registry,
        ILogger<RoundStateConsumer> log)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pitfall 6: dedicated channel per consumer.
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await _channel.ExchangeDeclareAsync(
            GatewayTopology.RoundExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await _channel.QueueDeclareAsync(
            GatewayTopology.RoundStateQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(
            GatewayTopology.RoundStateQueue,
            GatewayTopology.RoundExchange,
            "round.state.#",
            cancellationToken: stoppingToken);

        // PUSH, not poll — Pitfall 9.
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                await HandleDeliveryAsync(ea, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "Round-state delivery failed");
            }
        };
        await _channel.BasicConsumeAsync(
            GatewayTopology.RoundStateQueue,
            autoAck: true,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _log.LogInformation("Round-state consumer started on queue {Queue} (push mode)", GatewayTopology.RoundStateQueue);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }

    private async Task HandleDeliveryAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope<JsonElement>>(ea.Body.Span, JsonOptions);
        if (envelope is null) return;
        if (envelope.MessageType != MessageTypes.RoundStateChanged) return;

        var dto = envelope.Payload.Deserialize<RoundStateChangedPayload>(JsonOptions);
        if (dto is null) return;

        var newState = InboundTranslator.RoundStateStringToEnum(dto.State);

        // D-11: Settled→IterationOpen wipe BEFORE the broadcast so resumers see clean rings.
        if (_previousState == RoundProto.State.Settled && newState == RoundProto.State.IterationOpen)
        {
            _registry.OnSettledToIterationOpen();
        }
        _previousState = newState;

        var marketEvent = OutboundTranslator.FromRoundState(envelope);

        var teams = _registry.SnapshotAll();
        for (var i = 0; i < teams.Length; i++)
        {
            var teamState = teams[i];
            lock (teamState.StateLock)
            {
                var wrapper = new Envelope<object>(
                    MessageType: envelope.MessageType,
                    TimestampUtc: envelope.TimestampUtc,
                    CorrelationId: envelope.CorrelationId,
                    ClientId: teamState.ClientId,
                    InstrumentId: envelope.InstrumentId,
                    Sequence: null,
                    Payload: marketEvent);
                teamState.Ring.Append(wrapper);
            }
            // Pitfall 10: write outside the lock.
            if (teamState.Outbound is { } writer)
            {
                await writer.WriteAsync(marketEvent, ct);
            }
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
                _log.LogWarning(ex, "RoundStateConsumer channel close failed");
            }
            _channel.Dispose();
            _channel = null;
        }
        await base.StopAsync(cancellationToken);
    }
}
