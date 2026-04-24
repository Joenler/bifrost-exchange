// Phase 02 contracts-internal DTO shapes observed (verbatim Arena fork at SHA 5f8da60):
//   - SubmitOrderCommand(string ClientId, InstrumentIdDto InstrumentId, string Side,
//                        string OrderType, long? PriceTicks, decimal Quantity,
//                        decimal? DisplaySliceSize)
//   - CancelOrderCommand(string ClientId, long OrderId, InstrumentIdDto InstrumentId)
//   - ReplaceOrderCommand(string ClientId, long OrderId, long? NewPriceTicks,
//                         decimal? NewQuantity, InstrumentIdDto InstrumentId)
//   - InstrumentIdDto(string DeliveryArea, DateTimeOffset DeliveryPeriodStart,
//                     DateTimeOffset DeliveryPeriodEnd)
//
// CorrelationId observed shape (Bifrost.Quoter.Pricing.CorrelationId, Plan 03 co-donation):
//   - readonly record struct CorrelationId(string Value)  -- string-wrapping
//
// Determinism strategy (per 03-RESEARCH.md):
//   - CorrelationId is constructed from the string template
//     "quoter-{tickNs}-{deliveryArea}-{deliveryStartTicks}-{side}" -- deterministic given
//     the injected clock + instrument + side. The orderId field on the DTO comes from the
//     OrderId surface produced by the matching engine; until then OrderId is the long
//     value carried by Bifrost.Exchange.Domain.OrderId (already a long-wrapping struct).

using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Exchange.Domain;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Pricing;
using Bifrost.Time;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Bifrost.Quoter.Rabbit;

/// <summary>
/// Live <see cref="IOrderContext"/> implementation that publishes the quoter's
/// outbound order commands onto the Phase 02 internal RabbitMQ command fabric
/// (<see cref="RabbitMqTopology.CommandExchange"/>) using the same JSON-encoded
/// <c>Bifrost.Contracts.Internal.Commands.*</c> DTOs the matching engine's
/// <c>CommandConsumerService</c> deserializes.
///
/// All commands carry <c>ClientId = "quoter"</c> -- the microprice self-filter
/// in <c>MicropriceCalculator</c> excludes orders matching this id so the
/// quoter does not quote off its own resting orders.
///
/// CorrelationId construction is deterministic: the string template embeds the
/// injected clock's logical nanoseconds plus instrument + side, so two runs of
/// the same scenario seeded with the same FakeTimeProvider produce identical
/// outbound command streams (QTR-01 byte-equivalence).
/// </summary>
public sealed class QuoterCommandPublisher : IOrderContext
{
    private const string ClientIdLiteral = "quoter";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IChannel _channel;
    private readonly IClock _clock;
    private readonly ILogger<QuoterCommandPublisher> _log;

    public QuoterCommandPublisher(
        IChannel channel,
        IClock clock,
        ILogger<QuoterCommandPublisher> log)
    {
        _channel = channel;
        _clock = clock;
        _log = log;
    }

    public ILogger Logger => _log;

    public CorrelationId SubmitLimitOrder(InstrumentId instrument, Side side, long priceTicks, decimal qty)
    {
        var correlationId = BuildDeterministicCorrelationId(instrument, side);
        var dto = new SubmitOrderCommand(
            ClientId: ClientIdLiteral,
            InstrumentId: ToInstrumentIdDto(instrument),
            Side: side.ToString(),
            OrderType: "Limit",
            PriceTicks: priceTicks,
            Quantity: qty,
            DisplaySliceSize: null);

        Publish(RabbitMqTopology.RoutingKeyOrderSubmit, dto, correlationId);
        return correlationId;
    }

    public void CancelOrder(InstrumentId instrument, OrderId orderId)
    {
        var correlationId = BuildDeterministicCorrelationId(instrument, Side.Buy, suffix: "cancel");
        var dto = new CancelOrderCommand(
            ClientId: ClientIdLiteral,
            OrderId: orderId.Value,
            InstrumentId: ToInstrumentIdDto(instrument));

        Publish(RabbitMqTopology.RoutingKeyOrderCancel, dto, correlationId);
    }

    public void ReplaceOrder(InstrumentId instrument, OrderId orderId, long newPriceTicks, decimal? newQty)
    {
        var correlationId = BuildDeterministicCorrelationId(instrument, Side.Buy, suffix: "replace");
        var dto = new ReplaceOrderCommand(
            ClientId: ClientIdLiteral,
            OrderId: orderId.Value,
            NewPriceTicks: newPriceTicks,
            NewQuantity: newQty,
            InstrumentId: ToInstrumentIdDto(instrument));

        Publish(RabbitMqTopology.RoutingKeyOrderReplace, dto, correlationId);
    }

    /// <summary>
    /// The Quoter reads tracked orders from its own
    /// <c>PyramidQuoteTracker.TryGetTrackedSlot</c> accessor (Plan 5 Option B);
    /// <see cref="IOrderContext.GetOrder"/> is never called in the quoter's
    /// runtime flow. Throwing here (rather than silently returning null)
    /// catches any accidental future caller that would otherwise bypass the
    /// tracker and read stale state from the publisher boundary.
    /// </summary>
    public Order? GetOrder(OrderId orderId) =>
        throw new NotSupportedException(
            "Quoter reads tracked orders from its own PyramidQuoteTracker; IOrderContext.GetOrder is not called in Quoter's flow.");

    private void Publish(string routingKey, object dto, CorrelationId correlationId)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(dto, dto.GetType(), JsonOptions);
        var props = new BasicProperties
        {
            ContentType = "application/json",
            CorrelationId = correlationId.Value,
        };

        // BasicPublishAsync is async over channel I/O. The quoter loop is
        // single-writer per tick and tolerates a synchronous wait here -- the
        // publish call is non-blocking on the broker side (frame buffered into
        // the connection's outgoing TCP socket), and BufferedEventPublisher
        // proves the same wait pattern works for high-throughput emission.
        _channel.BasicPublishAsync(
                exchange: RabbitMqTopology.CommandExchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: props,
                body: body)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    private CorrelationId BuildDeterministicCorrelationId(
        InstrumentId instrument,
        Side side,
        string suffix = "submit")
    {
        var tickNs = _clock.GetUtcNow().UtcTicks * 100;
        var area = instrument.DeliveryArea.Value;
        var startTicks = instrument.DeliveryPeriod.Start.UtcTicks;
        var value = $"quoter-{tickNs}-{area}-{startTicks}-{side}-{suffix}";
        return new CorrelationId(value);
    }

    private static InstrumentIdDto ToInstrumentIdDto(InstrumentId instrument) =>
        new(
            DeliveryArea: instrument.DeliveryArea.Value,
            DeliveryPeriodStart: instrument.DeliveryPeriod.Start,
            DeliveryPeriodEnd: instrument.DeliveryPeriod.End);
}
