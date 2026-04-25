using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Translation;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Public-event broadcast consumer. <see cref="AsyncEventingBasicConsumer"/>
/// push pattern (Pitfall 9 — NEVER <c>BasicGetAsync</c> poll). Owns its OWN
/// <see cref="IChannel"/> from the shared <see cref="IConnection"/> (Pitfall 6).
///
/// Binds <see cref="RabbitMqTopology.PublicExchange"/> with patterns
/// <c>book.delta.#</c>, <c>trade.#</c>, <c>events.#</c>, <c>public.imbalance.#</c>.
/// Does NOT bind <c>public.forecast</c>; <c>ForecastDispatcher</c> (Plan 07)
/// owns the cohort-jittered forecast fan-out.
///
/// On each delivery: translate → for each registered team:
/// ring-Append under <see cref="TeamState.StateLock"/> → RELEASE the lock →
/// write to <see cref="TeamState.Outbound"/> (Pitfall 10).
/// </summary>
public sealed class PublicEventConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // book.delta.# / trade.# / events.# / public.imbalance.# — but NOT public.forecast
    // (owned by Plan 07 ForecastDispatcher).
    private static readonly string[] PublicBindingKeys = new[]
    {
        "public.book.delta.#",
        "public.trade.#",
        "events.#",
        "public.imbalance.#",
    };

    private readonly IConnection _connection;
    private readonly TeamRegistry _registry;
    private readonly IClock _clock;
    private readonly ILogger<PublicEventConsumer> _log;
    private IChannel? _channel;

    public PublicEventConsumer(
        IConnection connection,
        TeamRegistry registry,
        IClock clock,
        ILogger<PublicEventConsumer> log)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pitfall 6: dedicated channel per consumer.
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await _channel.ExchangeDeclareAsync(
            RabbitMqTopology.PublicExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await _channel.QueueDeclareAsync(
            GatewayTopology.PublicEventsQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken);

        for (var i = 0; i < PublicBindingKeys.Length; i++)
        {
            await _channel.QueueBindAsync(
                GatewayTopology.PublicEventsQueue,
                RabbitMqTopology.PublicExchange,
                PublicBindingKeys[i],
                cancellationToken: stoppingToken);
        }

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
                _log.LogError(ex, "Public-event delivery failed");
            }
        };
        await _channel.BasicConsumeAsync(
            GatewayTopology.PublicEventsQueue,
            autoAck: true,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _log.LogInformation("Public-event consumer started on queue {Queue} (push mode)", GatewayTopology.PublicEventsQueue);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }

    private async Task HandleDeliveryAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope<JsonElement>>(ea.Body.Span, JsonOptions);
        if (envelope is null) return;

        // Translate per envelope.MessageType.
        StrategyProto.MarketEvent? marketEvent = envelope.MessageType switch
        {
            MessageTypes.BookDelta => OutboundTranslator.FromBookDelta(envelope),
            MessageTypes.PublicTrade => OutboundTranslator.FromPublicTrade(envelope),
            MessageTypes.RegimeChange => OutboundTranslator.FromRegimeChange(envelope),
            MessageTypes.PhysicalShock => OutboundTranslator.FromPhysicalShock(envelope),
            MessageTypes.ForecastRevision => OutboundTranslator.FromForecastRevision(envelope),
            MessageTypes.ImbalancePrint => OutboundTranslator.FromImbalancePrint(envelope),
            // public.forecast is handled by ForecastDispatcher (Plan 07), not here.
            _ => null,
        };
        if (marketEvent is null) return;

        // Broadcast: append to every team's ring under that team's StateLock,
        // RELEASE the lock, then write to that team's Outbound (Pitfall 10).
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
        _ = _clock; // referenced for DI symmetry; consumer doesn't need clock for routing.
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
                _log.LogWarning(ex, "PublicEventConsumer channel close failed");
            }
            _channel.Dispose();
            _channel = null;
        }
        await base.StopAsync(cancellationToken);
    }
}
