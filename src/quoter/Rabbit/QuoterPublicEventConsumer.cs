using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Domain;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Quoter.Pricing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
// Disambiguate quoter-side event records (correlation-id-bearing reconciliation
// events in Bifrost.Quoter.Pricing.Events) from the matching-engine-internal
// wire DTOs (Bifrost.Contracts.Internal.Events.OrderAcceptedEvent etc.) and from
// the Domain-level OrderAccepted/OrderRejected/OrderCancelled records. Without
// these aliases the translation helpers below collide on simple names (CS0104).
using QuoterOrderAccepted = Bifrost.Quoter.Pricing.Events.OrderAccepted;
using QuoterOrderCancelled = Bifrost.Quoter.Pricing.Events.OrderCancelled;
using QuoterOrderRejected = Bifrost.Quoter.Pricing.Events.OrderRejected;
using QuoterOrderFill = Bifrost.Quoter.Pricing.Events.OrderFill;

namespace Bifrost.Quoter.Rabbit;

/// <summary>
/// Poll-mode <see cref="BackgroundService"/> consumer for inbound private
/// order-lifecycle events addressed to ClientId="quoter" on the exchange's
/// bifrost.private topic exchange. Decodes the generic <c>Envelope</c> JSON,
/// then per <c>MessageType</c> subtype-deserializes the payload into a
/// <c>Bifrost.Contracts.Internal.Events.*Event</c> record, translates the wire
/// DTO into the matching <c>Bifrost.Quoter.Pricing.Events.*</c> quoter-internal
/// record, and dispatches to <see cref="PyramidQuoteTracker"/>'s four lifecycle
/// callbacks so production has the same <c>_pending</c> / <c>_accepted</c>
/// bookkeeping invariants as the integration suite's TestRabbitPublisher.
///
/// LOCK ORDER (matches Quoter.cs file-header contract):
///   _globalRegimeLock  →  _perInstrumentState[i]   (i in _sortedInstruments order)
/// This consumer does NOT take _globalRegimeLock. Tracker mutations touch only
/// the tracker's own ConcurrentDictionary/ImmutableDictionary state, all via
/// non-compound (TryAdd / TryRemove / TryGetValue + indexer-set) operations
/// already in PyramidQuoteTracker. No per-instrument Monitor lock is held
/// while dispatching — this consumer is a reader of a single-writer tracker
/// surface that tolerates concurrent mutation by design.
///
/// CorrelationId invariant: the exchange carries the correlation id on the
/// envelope (see RabbitMqEventPublisher.PublishPrivate), not on the payload
/// record. Dispatch helpers read <c>envelope.CorrelationId</c> and pass it into
/// the quoter-side record, which is how <c>_pending</c> → <c>_accepted</c>
/// transitions locate their slot on <c>OnOrderAccepted</c>.
///
/// Wire topology:
///   Exchange: RabbitMqTopology.PrivateExchange ("bifrost.private", topic, durable)
///   Queue:    "bifrost.quoter.private" (non-durable, auto-delete, exclusive=false)
///   Bindings: "private.order.quoter.*"   (accepted / cancelled / rejected)
///             "private.exec.quoter.fill" (OrderExecuted)
/// </summary>
public sealed class QuoterPublicEventConsumer(
    IConnection connection,
    PyramidQuoteTracker tracker,
    ILogger<QuoterPublicEventConsumer> logger) : BackgroundService
{
    private const string QuoterClientId = "quoter";
    private const string QueueName = "bifrost.quoter.private";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Own IChannel per consumer — RabbitMQ.Client 7.x channels are NOT
        // thread-safe. Same rule as QuoterCommandPublisher's dedicated channel
        // and McRegimeForceConsumer's dedicated channel.
        _channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await _channel.ExchangeDeclareAsync(
            RabbitMqTopology.PrivateExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        // RabbitMQ 4 rejects transient non-exclusive queues — see the matching
        // comment in McRegimeForceConsumer. Per-instance ephemeral receiver:
        // exclusive makes the queue die with the connection, which is exactly
        // what we want for order-lifecycle fanout into a single-process quoter.
        await _channel.QueueDeclareAsync(
            QueueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken);

        // Two bindings — order lifecycle (accepted/cancelled/rejected) + fill exec path.
        await _channel.QueueBindAsync(
            QueueName,
            RabbitMqTopology.PrivateExchange,
            $"private.order.{QuoterClientId}.*",
            cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(
            QueueName,
            RabbitMqTopology.PrivateExchange,
            RabbitMqTopology.PrivateExecRoutingKey(QuoterClientId, "fill"),
            cancellationToken: stoppingToken);

        logger.LogInformation(
            "Quoter private-event consumer started on queue {Queue} (poll mode)",
            QueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _channel.BasicGetAsync(QueueName, autoAck: true, stoppingToken);
                if (result is null)
                {
                    await Task.Delay(50, stoppingToken);
                    continue;
                }

                Dispatch(result.Body.Span);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to deserialize quoter private-event message");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in quoter private-event poll loop");
                await Task.Delay(100, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Decode + dispatch seam — <c>internal</c> so unit tests can drive the
    /// translation path without a live broker (via <c>InternalsVisibleTo</c>
    /// on <c>Bifrost.Quoter.csproj</c>). Deserializes the outer envelope as
    /// <c>Envelope&lt;JsonElement&gt;</c>, then per <c>MessageType</c> subtype-
    /// deserializes the payload into the matching <c>Bifrost.Contracts.Internal
    /// .Events.*Event</c> record.
    /// </summary>
    internal void Dispatch(ReadOnlySpan<byte> body)
    {
        var envelope = JsonSerializer.Deserialize<Envelope<JsonElement>>(body, JsonOptions);
        if (envelope is null)
            return;

        // Defensive client-id filter — bifrost.private routing is topic-based
        // and already ClientId-targeted via the binding pattern, but this guard
        // prevents tracker mutation on foreign messages if routing ever leaks.
        // The wire DTOs each carry their own ClientId field on the payload; we
        // peek at it without fully materializing the record so unknown shapes
        // (e.g., newly-added event types) fall through the default arm cleanly.
        if (TryReadClientId(envelope.Payload) is { } clientId &&
            !string.IsNullOrEmpty(clientId) &&
            !string.Equals(clientId, QuoterClientId, StringComparison.Ordinal))
        {
            return;
        }

        switch (envelope.MessageType)
        {
            case MessageTypes.OrderAccepted:
                DispatchOrderAccepted(envelope.Payload, envelope.CorrelationId);
                break;
            case MessageTypes.OrderCancelled:
                DispatchOrderCancelled(envelope.Payload, envelope.CorrelationId);
                break;
            case MessageTypes.OrderRejected:
                DispatchOrderRejected(envelope.Payload, envelope.CorrelationId);
                break;
            case MessageTypes.OrderExecuted:
                DispatchOrderFill(envelope.Payload, envelope.CorrelationId);
                break;
            default:
                // Unknown MessageType — not this consumer's business.
                break;
        }
    }

    private void DispatchOrderAccepted(JsonElement payload, string? envelopeCorrelationId)
    {
        var e = payload.Deserialize<OrderAcceptedEvent>(JsonOptions);
        if (e is null)
            return;

        var inst = ToInstrumentId(e.InstrumentId);
        var corr = envelopeCorrelationId is null ? (CorrelationId?)null : new CorrelationId(envelopeCorrelationId);

        tracker.OnOrderAccepted(new QuoterOrderAccepted(
            OrderId: new OrderId(e.OrderId),
            Instrument: inst,
            Side: ParseSide(e.Side),
            OrderType: ParseOrderType(e.OrderType),
            PriceTicks: e.PriceTicks,
            Quantity: e.Quantity,
            DisplaySliceSize: e.DisplaySliceSize,
            CorrelationId: corr,
            ExchangeTimestampNs: e.TimestampNs));
    }

    private void DispatchOrderCancelled(JsonElement payload, string? envelopeCorrelationId)
    {
        var e = payload.Deserialize<OrderCancelledEvent>(JsonOptions);
        if (e is null)
            return;

        var inst = ToInstrumentId(e.InstrumentId);
        var corr = envelopeCorrelationId is null ? (CorrelationId?)null : new CorrelationId(envelopeCorrelationId);

        tracker.OnOrderCancelled(new QuoterOrderCancelled(
            OrderId: new OrderId(e.OrderId),
            Instrument: inst,
            RemainingQuantity: e.RemainingQuantity,
            CorrelationId: corr,
            ExchangeTimestampNs: e.TimestampNs));
    }

    private void DispatchOrderRejected(JsonElement payload, string? envelopeCorrelationId)
    {
        var e = payload.Deserialize<OrderRejectedEvent>(JsonOptions);
        if (e is null)
            return;

        var corr = envelopeCorrelationId is null ? (CorrelationId?)null : new CorrelationId(envelopeCorrelationId);

        tracker.OnOrderRejected(new QuoterOrderRejected(
            OrderId: new OrderId(e.OrderId),
            Reason: e.Reason,
            CorrelationId: corr,
            ExchangeTimestampNs: e.TimestampNs));
    }

    private void DispatchOrderFill(JsonElement payload, string? envelopeCorrelationId)
    {
        var e = payload.Deserialize<OrderExecutedEvent>(JsonOptions);
        if (e is null)
            return;

        var inst = ToInstrumentId(e.InstrumentId);
        var corr = envelopeCorrelationId is null ? (CorrelationId?)null : new CorrelationId(envelopeCorrelationId);

        tracker.OnFill(new QuoterOrderFill(
            TradeId: new TradeId(e.TradeId),
            OrderId: new OrderId(e.OrderId),
            Instrument: inst,
            PriceTicks: e.PriceTicks,
            FilledQuantity: e.FilledQuantity,
            RemainingQuantity: e.RemainingQuantity,
            Side: ParseSide(e.Side),
            IsAggressor: e.IsAggressor,
            Fee: e.Fee,
            CorrelationId: corr,
            ExchangeTimestampNs: e.TimestampNs));
    }

    private static Side ParseSide(string? s) =>
        string.Equals(s, "Sell", StringComparison.OrdinalIgnoreCase) ? Side.Sell : Side.Buy;

    private static OrderType ParseOrderType(string? s) =>
        Enum.TryParse<OrderType>(s, ignoreCase: true, out var parsed) ? parsed : OrderType.Limit;

    /// <summary>
    /// Inverse of <c>QuoterCommandPublisher.ToInstrumentIdDto</c>: rebuilds a
    /// domain <see cref="InstrumentId"/> from the wire-level
    /// <see cref="InstrumentIdDto"/> subobject that the exchange serializes
    /// inline on every <c>*Event</c> payload.
    /// </summary>
    private static InstrumentId ToInstrumentId(InstrumentIdDto dto) =>
        new(
            new DeliveryArea(dto.DeliveryArea),
            new DeliveryPeriod(dto.DeliveryPeriodStart, dto.DeliveryPeriodEnd));

    /// <summary>
    /// Peek the <c>clientId</c> field out of the payload JSON without fully
    /// materializing the record. Returns <c>null</c> when the payload shape
    /// has no <c>clientId</c> field (e.g., a future event type without the
    /// field) so the dispatch-arm default path still runs.
    /// </summary>
    private static string? TryReadClientId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!payload.TryGetProperty("clientId", out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken);
            _channel = null;
        }
    }
}
