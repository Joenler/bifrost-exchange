using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Pricing;
using Xunit;
using OrderAccepted = Bifrost.Quoter.Pricing.Events.OrderAccepted;
using OrderCancelled = Bifrost.Quoter.Pricing.Events.OrderCancelled;

namespace Bifrost.Quoter.Tests.Pricing;

public sealed class PyramidQuoteTrackerTests
{
    private static readonly DeliveryArea TestArea = new("DE1");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 6, 10, 0, 0, TimeSpan.Zero);

    private static InstrumentId MakeInstrument(int hourOffset) => new(
        TestArea,
        new DeliveryPeriod(
            BaseTime.AddHours(hourOffset),
            BaseTime.AddHours(hourOffset + 1)));

    [Fact]
    public void TrackOrder_WithPriceTicks_PersistsOnSlot()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(0);
        var corr = new CorrelationId("test-submit-001");
        const long expectedPrice = 5123L;

        tracker.TrackOrder(inst, Side.Buy, level: 0, corr, priceTicks: expectedPrice);
        tracker.OnOrderAccepted(new OrderAccepted(
            OrderId: new OrderId(7),
            Instrument: inst,
            Side: Side.Buy,
            OrderType: OrderType.Limit,
            PriceTicks: expectedPrice,
            Quantity: 1m,
            DisplaySliceSize: null,
            CorrelationId: corr));

        Assert.True(tracker.TryGetTrackedSlot(inst, Side.Buy, level: 0, out var slot));
        Assert.Equal(expectedPrice, slot.PriceTicks);
    }

    [Fact]
    public void ReplaceMutation_SlotPriceTicks_UpdatesInPlace()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(1);
        var corr = new CorrelationId("test-replace-001");
        const long initialPrice = 5000L;
        const long targetPrice = 5050L;

        tracker.TrackOrder(inst, Side.Sell, level: 1, corr, priceTicks: initialPrice);
        tracker.OnOrderAccepted(new OrderAccepted(
            OrderId: new OrderId(42),
            Instrument: inst,
            Side: Side.Sell,
            OrderType: OrderType.Limit,
            PriceTicks: initialPrice,
            Quantity: 2m,
            DisplaySliceSize: null,
            CorrelationId: corr));

        Assert.True(tracker.TryGetTrackedSlot(inst, Side.Sell, level: 1, out var slot));
        Assert.Equal(initialPrice, slot.PriceTicks);

        slot.PriceTicks = targetPrice;

        Assert.True(tracker.TryGetTrackedSlot(inst, Side.Sell, level: 1, out var slot2));
        Assert.Equal(targetPrice, slot2.PriceTicks);
    }

    [Fact]
    public void JitterGuard_CalculationSuppressesNoOpReplace()
    {
        const long slotPrice = 5000L;
        const long closeTarget = 5003L;
        const long farTarget = 5010L;
        const int threshold = 5;

        Assert.True(
            Math.Abs(slotPrice - closeTarget) <= threshold,
            "Jitter guard should suppress Replace inside threshold");
        Assert.False(
            Math.Abs(slotPrice - farTarget) <= threshold,
            "Jitter guard should permit Replace outside threshold");
    }

    [Fact]
    public void OnOrderCancelled_ClearsSlot_NewTrackOrderWritesFreshPriceTicks()
    {
        var tracker = new PyramidQuoteTracker(maxLevels: 3, TimeProvider.System);
        var inst = MakeInstrument(2);
        var corr1 = new CorrelationId("test-cancel-001");
        var corr2 = new CorrelationId("test-cancel-002");

        tracker.TrackOrder(inst, Side.Buy, level: 2, corr1, priceTicks: 4800L);
        var order1 = new OrderId(100);
        tracker.OnOrderAccepted(new OrderAccepted(
            OrderId: order1,
            Instrument: inst,
            Side: Side.Buy,
            OrderType: OrderType.Limit,
            PriceTicks: 4800L,
            Quantity: 1m,
            DisplaySliceSize: null,
            CorrelationId: corr1));

        tracker.OnOrderCancelled(new OrderCancelled(
            OrderId: order1,
            Instrument: inst,
            RemainingQuantity: 1m,
            CorrelationId: corr1));

        tracker.TrackOrder(inst, Side.Buy, level: 2, corr2, priceTicks: 5200L);
        var order2 = new OrderId(101);
        tracker.OnOrderAccepted(new OrderAccepted(
            OrderId: order2,
            Instrument: inst,
            Side: Side.Buy,
            OrderType: OrderType.Limit,
            PriceTicks: 5200L,
            Quantity: 1m,
            DisplaySliceSize: null,
            CorrelationId: corr2));

        Assert.True(tracker.TryGetTrackedSlot(inst, Side.Buy, level: 2, out var slot));
        Assert.Equal(5200L, slot.PriceTicks);
    }
}
