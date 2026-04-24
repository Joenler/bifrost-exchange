using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Rabbit;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Xunit;
// Disambiguate quoter-side events from matching-engine-internal Domain records.
using QuoterOrderAccepted = Bifrost.Quoter.Pricing.Events.OrderAccepted;

namespace Bifrost.Quoter.Tests.Rabbit;

/// <summary>
/// Tests for QuoterPublicEventConsumer.Dispatch — drive the decode +
/// translation + tracker-callback seam WITHOUT a live broker by calling the
/// internal Dispatch method (exposed via InternalsVisibleTo). Verifies the
/// production-wiring path has the same _pending / _accepted bookkeeping
/// invariants as the TestRabbitPublisher fixture.
///
/// Wire-format alignment: envelopes constructed here mirror exactly what
/// RabbitMqEventPublisher.PublishPrivate emits on bifrost.private — the
/// per-event payload record (OrderAcceptedEvent / OrderExecutedEvent /
/// OrderCancelledEvent / OrderRejectedEvent) wrapped in Envelope&lt;T&gt; with
/// the correlation id on the envelope (not on the payload).
/// </summary>
public sealed class QuoterPublicEventConsumerTests
{
    private const string QuoterClientId = "quoter";

    private static readonly DeliveryArea TestArea = new("DE1");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 6, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static InstrumentId MakeInstrument(int hourOffset) => new(
        TestArea,
        new DeliveryPeriod(
            BaseTime.AddHours(hourOffset),
            BaseTime.AddHours(hourOffset + 1)));

    private static InstrumentIdDto ToDto(InstrumentId inst) => new(
        DeliveryArea: inst.DeliveryArea.Value,
        DeliveryPeriodStart: inst.DeliveryPeriod.Start,
        DeliveryPeriodEnd: inst.DeliveryPeriod.End);

    private static QuoterPublicEventConsumer MakeConsumer(PyramidQuoteTracker tracker)
    {
        // IConnection is never used by Dispatch() — only ExecuteAsync creates
        // channels. The null-forgiving cast is safe because the tests never
        // call StartAsync on the BackgroundService.
        return new QuoterPublicEventConsumer(
            connection: (IConnection)null!,
            tracker: tracker,
            logger: NullLogger<QuoterPublicEventConsumer>.Instance);
    }

    private static byte[] SerializeEnvelope<T>(
        string messageType,
        T payload,
        string? correlationId = null,
        string? clientIdOnEnvelope = QuoterClientId)
    {
        var envelope = new Envelope<T>(
            MessageType: messageType,
            TimestampUtc: BaseTime,
            CorrelationId: correlationId,
            ClientId: clientIdOnEnvelope,
            InstrumentId: null,
            Sequence: null,
            Payload: payload);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    [Fact]
    public void Dispatch_OrderAcceptedEnvelope_CallsTrackerOnOrderAccepted()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(0);
        var corr = new CorrelationId("c-accept-001");

        // Pre-seed _pending via TrackOrder (matches Quoter.SubmitOrReplace fresh-submit path).
        tracker.TrackOrder(inst, Side.Buy, level: 0, corr, priceTicks: 5000L);

        var payload = new OrderAcceptedEvent(
            OrderId: 77,
            ClientId: QuoterClientId,
            InstrumentId: ToDto(inst),
            Side: "Buy",
            OrderType: "Limit",
            PriceTicks: 5000,
            Quantity: 1m,
            DisplaySliceSize: null,
            TimestampNs: 0);
        var bytes = SerializeEnvelope(MessageTypes.OrderAccepted, payload, correlationId: corr.Value);

        var consumer = MakeConsumer(tracker);
        consumer.Dispatch(bytes);

        Assert.True(tracker.TryGetTrackedSlot(inst, Side.Buy, level: 0, out var slot));
        Assert.NotNull(slot.OrderId);
        Assert.Equal(77L, slot.OrderId!.Value.Value);
    }

    [Fact]
    public void Dispatch_OrderCancelledEnvelope_CallsTrackerOnOrderCancelled()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(1);
        var corr = new CorrelationId("c-cancel-001");
        tracker.TrackOrder(inst, Side.Sell, level: 1, corr, priceTicks: 5050L);
        tracker.OnOrderAccepted(new QuoterOrderAccepted(
            OrderId: new OrderId(88),
            Instrument: inst,
            Side: Side.Sell,
            OrderType: OrderType.Limit,
            PriceTicks: 5050L,
            Quantity: 1m,
            DisplaySliceSize: null,
            CorrelationId: corr));

        Assert.True(tracker.TryGetTrackedSlot(inst, Side.Sell, level: 1, out _));

        var cancelPayload = new OrderCancelledEvent(
            OrderId: 88,
            ClientId: QuoterClientId,
            InstrumentId: ToDto(inst),
            RemainingQuantity: 1m,
            TimestampNs: 0);
        var bytes = SerializeEnvelope(MessageTypes.OrderCancelled, cancelPayload, correlationId: corr.Value);

        var consumer = MakeConsumer(tracker);
        consumer.Dispatch(bytes);

        Assert.False(tracker.TryGetTrackedSlot(inst, Side.Sell, level: 1, out _));
    }

    [Fact]
    public void Dispatch_OrderRejectedEnvelope_CallsTrackerOnOrderRejected()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(2);
        var corr = new CorrelationId("c-reject-001");
        tracker.TrackOrder(inst, Side.Buy, level: 0, corr, priceTicks: 4950L);

        var payload = new OrderRejectedEvent(
            OrderId: 0, // no OrderId assigned because rejection happens pre-accept
            ClientId: QuoterClientId,
            Reason: "InvalidQuantity",
            TimestampNs: 0);
        var bytes = SerializeEnvelope(MessageTypes.OrderRejected, payload, correlationId: corr.Value);

        var before = tracker.GetSlotSummary();
        Assert.True(before.Pending >= 1);

        var consumer = MakeConsumer(tracker);
        consumer.Dispatch(bytes);

        var after = tracker.GetSlotSummary();
        Assert.True(after.Pending < before.Pending,
            $"Expected pending to shrink after reject; before={before.Pending}, after={after.Pending}");
    }

    [Fact]
    public void Dispatch_OrderExecutedFillEnvelope_CallsTrackerOnFill()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(3);
        var corr = new CorrelationId("c-fill-001");
        tracker.TrackOrder(inst, Side.Sell, level: 2, corr, priceTicks: 5100L);
        tracker.OnOrderAccepted(new QuoterOrderAccepted(
            OrderId: new OrderId(99),
            Instrument: inst,
            Side: Side.Sell,
            OrderType: OrderType.Limit,
            PriceTicks: 5100L,
            Quantity: 1m,
            DisplaySliceSize: null,
            CorrelationId: corr));

        var payload = new OrderExecutedEvent(
            TradeId: 500,
            OrderId: 99,
            ClientId: QuoterClientId,
            InstrumentId: ToDto(inst),
            PriceTicks: 5100,
            FilledQuantity: 1m,
            RemainingQuantity: 0m,
            Side: "Sell",
            IsAggressor: false,
            Fee: 0.1m,
            TimestampNs: 0);
        var bytes = SerializeEnvelope(MessageTypes.OrderExecuted, payload, correlationId: corr.Value);

        var consumer = MakeConsumer(tracker);
        consumer.Dispatch(bytes);

        // RemainingQuantity == 0 → OnFill clears slot.
        Assert.False(tracker.TryGetTrackedSlot(inst, Side.Sell, level: 2, out _));
    }

    [Fact]
    public void Dispatch_UnknownMessageType_NoOpsTrackerState()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(0);
        tracker.TrackOrder(inst, Side.Buy, 0, new CorrelationId("c-x-001"), priceTicks: 5000L);
        var before = tracker.GetSlotSummary();

        // Construct a payload shaped like an Accepted event but with an
        // unknown MessageType discriminator; the consumer's default arm MUST
        // leave tracker state untouched.
        var payload = new OrderAcceptedEvent(
            OrderId: 0,
            ClientId: QuoterClientId,
            InstrumentId: ToDto(inst),
            Side: "Buy",
            OrderType: "Limit",
            PriceTicks: 5000,
            Quantity: 1m,
            DisplaySliceSize: null,
            TimestampNs: 0);
        var bytes = SerializeEnvelope("UnknownThing", payload);

        var consumer = MakeConsumer(tracker);
        consumer.Dispatch(bytes);

        var after = tracker.GetSlotSummary();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Dispatch_ForeignClientIdEnvelope_NoOpsTrackerState()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(0);
        var corr = new CorrelationId("c-foreign-001");
        tracker.TrackOrder(inst, Side.Buy, 0, corr, priceTicks: 5000L);
        var before = tracker.GetSlotSummary();

        // Payload.ClientId = "team-alpha" — the defensive filter on the
        // consumer MUST skip dispatch regardless of the envelope's ClientId.
        var payload = new OrderAcceptedEvent(
            OrderId: 77,
            ClientId: "team-alpha",
            InstrumentId: ToDto(inst),
            Side: "Buy",
            OrderType: "Limit",
            PriceTicks: 5000,
            Quantity: 1m,
            DisplaySliceSize: null,
            TimestampNs: 0);
        var bytes = SerializeEnvelope(
            MessageTypes.OrderAccepted,
            payload,
            correlationId: corr.Value,
            clientIdOnEnvelope: "team-alpha");

        var consumer = MakeConsumer(tracker);
        consumer.Dispatch(bytes);

        var after = tracker.GetSlotSummary();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Dispatch_30AcceptsThen30Cancels_PendingAndAcceptedEmpty()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(0);
        var consumer = MakeConsumer(tracker);

        // 30 iterations cycling through 3 levels x 2 sides = 6 slots. Each
        // iteration tracks -> accepts -> cancels so the slot is freed and
        // available for the next iteration. Proves _pending / _accepted stay
        // bounded under a sustained workload (WR-01 invariant).
        for (var i = 0; i < 30; i++)
        {
            var slotLevel = i % 3;
            var side = (i % 2 == 0) ? Side.Buy : Side.Sell;
            var corr = new CorrelationId($"c-bulk-{i:D3}");
            var orderId = 1000L + i;

            tracker.TrackOrder(inst, side, slotLevel, corr, priceTicks: 5000L + i);

            var acceptPayload = new OrderAcceptedEvent(
                OrderId: orderId,
                ClientId: QuoterClientId,
                InstrumentId: ToDto(inst),
                Side: side == Side.Buy ? "Buy" : "Sell",
                OrderType: "Limit",
                PriceTicks: 5000L + i,
                Quantity: 1m,
                DisplaySliceSize: null,
                TimestampNs: 0);
            consumer.Dispatch(SerializeEnvelope(
                MessageTypes.OrderAccepted, acceptPayload, correlationId: corr.Value));

            var cancelPayload = new OrderCancelledEvent(
                OrderId: orderId,
                ClientId: QuoterClientId,
                InstrumentId: ToDto(inst),
                RemainingQuantity: 1m,
                TimestampNs: 0);
            consumer.Dispatch(SerializeEnvelope(
                MessageTypes.OrderCancelled, cancelPayload, correlationId: corr.Value));
        }

        var summary = tracker.GetSlotSummary();
        Assert.Equal(0, summary.Pending);
        Assert.Equal(0, summary.Working);
    }
}
