using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Internal.Shared;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Gateway.Guards;
using Bifrost.Gateway.Metrics;
using Bifrost.Gateway.Position;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Translation;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Private-event fan-out consumer. <see cref="AsyncEventingBasicConsumer"/>
/// push pattern (Pitfall 9 — NEVER <c>BasicGetAsync</c> poll). Owns its OWN
/// <see cref="IChannel"/> from the shared <see cref="IConnection"/> (Pitfall 6).
///
/// On each delivery: decode envelope → translate → ring-Append + position
/// bookkeeping under <see cref="TeamState.StateLock"/> → RELEASE the lock →
/// write to <see cref="TeamState.Outbound"/> (Pitfall 10).
///
/// Subscribes to <see cref="RabbitMqTopology.PrivateExchange"/> with the
/// catch-all binding pattern <c>private.#</c>; the <c>envelope.ClientId</c>
/// routes the dispatch to the matching team via
/// <see cref="TeamRegistry.TryGetByClientId"/>. At 8 teams the routing-key
/// fan-out happens RabbitMQ-side; one consumer with <c>private.#</c> binding
/// is one channel total instead of N channels.
/// </summary>
public sealed class PrivateEventConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Nullable for test construction (07-08 acceptance suites): tests instantiate
    // the consumer to drive DispatchEnvelopeAsync directly via the internal seam
    // and never invoke ExecuteAsync (where _connection IS used). Production DI
    // always supplies a non-null IConnection through Program.cs.
    private readonly IConnection? _connection;
    private readonly TeamRegistry _registry;
    private readonly IClock _clock;
    private readonly PositionTracker _tracker;
    private readonly ILogger<PrivateEventConsumer> _log;
    private IChannel? _channel;

    public PrivateEventConsumer(
        IConnection connection,
        TeamRegistry registry,
        IClock clock,
        PositionTracker tracker,
        ILogger<PrivateEventConsumer> log)
    {
        _connection = connection;   // null permitted for unit tests; ExecuteAsync rejects null
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_connection is null)
            throw new InvalidOperationException("PrivateEventConsumer: IConnection is required when running as a HostedService (production DI).");
        // Pitfall 6: dedicated channel per consumer.
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await _channel.ExchangeDeclareAsync(
            RabbitMqTopology.PrivateExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        // Single gateway-local queue; envelope.ClientId routes per-team.
        // RabbitMQ 4 needs exclusive=true for transient queues.
        const string queueName = "bifrost.gateway.private.all";
        await _channel.QueueDeclareAsync(
            queueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(
            queueName,
            RabbitMqTopology.PrivateExchange,
            "private.#",
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
                _log.LogError(ex, "Private-event delivery failed");
            }
        };
        await _channel.BasicConsumeAsync(
            queueName,
            autoAck: true,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _log.LogInformation("Private-event consumer started on queue {Queue} (push mode)", queueName);

        // Hold the BackgroundService alive until cancellation; consumer callbacks run on channel threads.
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }

    private async Task HandleDeliveryAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope<JsonElement>>(ea.Body.Span, JsonOptions);
        if (envelope is null) return;
        await DispatchEnvelopeAsync(envelope, ct);
    }

    /// <summary>
    /// Test seam (07-08 acceptance suites). The production delivery loop deserializes
    /// the wire body into an <see cref="Envelope{JsonElement}"/> then dispatches via
    /// this method — exactly the same code path real RabbitMQ deliveries take, minus
    /// the AMQP envelope decoding. <c>InternalsVisibleTo("Bifrost.Gateway.Tests")</c>
    /// in <c>Bifrost.Gateway.csproj</c> exposes this for direct invocation.
    /// </summary>
    internal async Task DispatchEnvelopeAsync(Envelope<JsonElement> envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrEmpty(envelope.ClientId)) return;
        if (!_registry.TryGetByClientId(envelope.ClientId, out var teamState) || teamState is null) return;

        // Translate per envelope.MessageType.
        StrategyProto.MarketEvent? marketEvent = envelope.MessageType switch
        {
            MessageTypes.OrderAccepted => OutboundTranslator.FromAccepted(envelope),
            MessageTypes.OrderRejected => OutboundTranslator.FromRejected(envelope),
            MessageTypes.OrderExecuted => OutboundTranslator.FromExecuted(envelope),
            MessageTypes.OrderCancelled => OutboundTranslator.FromCancelled(envelope),
            MessageTypes.MarketOrderRemainderCancelled => OutboundTranslator.FromCancelled(envelope),
            MessageTypes.ImbalanceSettlement => OutboundTranslator.FromImbalanceSettlement(envelope),
            _ => null,
        };
        if (marketEvent is null) return;

        // Lock-scoped: ring-Append + position-update + open-orders bookkeeping +
        // (if Fill) snapshot construction.
        StrategyProto.MarketEvent? snapshot = null;
        var publishToOutbound = envelope.MessageType != MessageTypes.ImbalanceSettlement;
        lock (teamState.StateLock)
        {
            // Append the marketEvent to the ring.
            var wrapper = new Envelope<object>(
                MessageType: envelope.MessageType,
                TimestampUtc: envelope.TimestampUtc,
                CorrelationId: envelope.CorrelationId,
                ClientId: envelope.ClientId,
                InstrumentId: envelope.InstrumentId,
                Sequence: null,
                Payload: marketEvent);
            teamState.Ring.Append(wrapper);

            switch (envelope.MessageType)
            {
                case MessageTypes.OrderAccepted:
                    {
                        var dto = envelope.Payload.Deserialize<OrderAcceptedEvent>(JsonOptions);
                        if (dto is not null)
                        {
                            var idx = InstrumentOrdering.IndexOfDto(dto.InstrumentId);
                            if (idx >= 0)
                            {
                                _tracker.OnOrderAccepted(teamState, new OpenOrder(
                                    OrderId: dto.OrderId,
                                    ClientOrderId: string.Empty,
                                    InstrumentIndex: idx,
                                    Side: dto.Side,
                                    PriceTicks: dto.PriceTicks ?? 0L,
                                    QuantityTicks: QuantityScale.ToTicks(dto.Quantity),
                                    DisplaySliceTicks: dto.DisplaySliceSize is null ? 0L : QuantityScale.ToTicks(dto.DisplaySliceSize.Value),
                                    SubmittedAtUtc: envelope.TimestampUtc));
                            }
                        }
                        break;
                    }
                case MessageTypes.OrderCancelled:
                case MessageTypes.MarketOrderRemainderCancelled:
                    {
                        var dto = envelope.Payload.Deserialize<OrderCancelledEvent>(JsonOptions);
                        if (dto is not null)
                        {
                            var idx = InstrumentOrdering.IndexOfDto(dto.InstrumentId);
                            if (idx >= 0)
                            {
                                _tracker.OnOrderCancelled(teamState, idx, dto.OrderId);
                            }
                        }
                        break;
                    }
                case MessageTypes.OrderExecuted:
                    {
                        var dto = envelope.Payload.Deserialize<OrderExecutedEvent>(JsonOptions);
                        if (dto is not null)
                        {
                            // Keep the OTR denominator current.
                            OtrGuard.RecordTrade(teamState, _clock);
                            var idx = InstrumentOrdering.IndexOfDto(dto.InstrumentId);
                            if (idx >= 0)
                            {
                                var filledTicks = QuantityScale.ToTicks(dto.FilledQuantity);
                                // Decrement the resting order's notional by the consumed portion.
                                _tracker.OnPartialOrFullFill(teamState, idx, dto.OrderId, filledTicks);
                                // Update net + vwap; build the PositionSnapshot envelope.
                                var instId = InstrumentOrdering.CanonicalIds[idx];
                                snapshot = _tracker.OnFill(
                                    teamState,
                                    dto.InstrumentId,
                                    instId,
                                    idx == 0 ? MarketProto.ProductType.Hour : MarketProto.ProductType.Quarter,
                                    InboundTranslator.SideStringToEnum(dto.Side),
                                    filledTicks,
                                    dto.PriceTicks);
                                // Append the snapshot to the ring as well so resume replays both
                                // the originating Fill and its derived snapshot.
                                var snapWrapper = new Envelope<object>(
                                    MessageType: MessageTypes.PositionSnapshot,
                                    TimestampUtc: envelope.TimestampUtc,
                                    CorrelationId: envelope.CorrelationId,
                                    ClientId: envelope.ClientId,
                                    InstrumentId: instId,
                                    Sequence: null,
                                    Payload: snapshot);
                                teamState.Ring.Append(snapWrapper);
                            }
                        }
                        break;
                    }
            }
        }
        // Pitfall 10: lock RELEASED before any channel write.
        if (publishToOutbound && teamState.Outbound is { } writer)
        {
            await writer.WriteAsync(marketEvent, ct);
            // D-06a: PositionSnapshot rides IMMEDIATELY after the Fill envelope.
            if (snapshot is not null)
            {
                await writer.WriteAsync(snapshot, ct);
            }
        }

        // SPEC req 12 metrics. prometheus-net Counter/Gauge are thread-safe; place
        // outside the lock so they never extend critical-section duration.
        if (envelope.MessageType == MessageTypes.OrderExecuted)
        {
            GatewayMetrics.Fills.WithLabels(teamState.TeamName).Inc();
        }
        // RingBufferOccupancy is updated on every Append site; PrivateEventConsumer
        // is the dominant ring-Append path (one append per private event + a second
        // for each Fill's PositionSnapshot). Reading head/tail outside the lock is
        // safe enough for a gauge — slight skew at high contention, never zero or
        // negative.
        GatewayMetrics.RingBufferOccupancy
            .WithLabels(teamState.TeamName)
            .Set(teamState.Ring.Head - teamState.Ring.Tail);
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
                _log.LogWarning(ex, "PrivateEventConsumer channel close failed");
            }
            _channel.Dispose();
            _channel = null;
        }
        await base.StopAsync(cancellationToken);
    }
}
