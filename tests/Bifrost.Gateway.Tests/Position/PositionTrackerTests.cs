using Bifrost.Contracts.Internal;
using Bifrost.Gateway.Position;
using Bifrost.Gateway.State;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;
using Xunit;

namespace Bifrost.Gateway.Tests.Position;

public class PositionTrackerTests
{
    private static TeamState NewState()
    {
        var registered = new DateTimeOffset(2026, 4, 25, 0, 0, 0, TimeSpan.Zero);
        return new TeamState("alpha", "team-alpha-1", registered);
    }

    private static InstrumentIdDto Dto(string id)
    {
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return id switch
        {
            "H1" => new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1)),
            "Q1" => new InstrumentIdDto("DE", hourStart, hourStart.AddMinutes(15)),
            "Q2" => new InstrumentIdDto("DE", hourStart.AddMinutes(15), hourStart.AddMinutes(30)),
            "Q3" => new InstrumentIdDto("DE", hourStart.AddMinutes(30), hourStart.AddMinutes(45)),
            "Q4" => new InstrumentIdDto("DE", hourStart.AddMinutes(45), hourStart.AddHours(1)),
            _ => throw new ArgumentException(id),
        };
    }

    private static StrategyProto.PositionSnapshot Snap(StrategyProto.MarketEvent ev) => ev.PositionSnapshot;

    [Fact]
    public void OnFill_BuyFromZero_SetsVwapToFillPrice()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        var ev = tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Buy, filledQtyTicks: 100, fillPriceTicks: 5_000);

        var snap = Snap(ev);
        Assert.Equal(100, snap.NetPositionTicks);
        Assert.Equal(5_000, snap.AveragePriceTicks);
        Assert.Equal(100, state.NetPositionTicks[0]);
        Assert.Equal(5_000, state.VwapTicks[0]);
    }

    [Fact]
    public void OnFill_SellFromZero_SetsNegativeNetAndVwapToFillPrice()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        var ev = tracker.OnFill(state, Dto("Q2"), "Q2", MarketProto.ProductType.Quarter,
            MarketProto.Side.Sell, filledQtyTicks: 50, fillPriceTicks: 4_500);

        var snap = Snap(ev);
        Assert.Equal(-50, snap.NetPositionTicks);
        Assert.Equal(4_500, snap.AveragePriceTicks);
    }

    [Fact]
    public void OnFill_BuyAddsToBuy_VwapWeightedRunningMean()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        // First buy: 100 @ 5_000 → VWAP = 5_000.
        tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Buy, filledQtyTicks: 100, fillPriceTicks: 5_000);
        // Second buy: 100 @ 6_000 → VWAP = (100*5_000 + 100*6_000) / 200 = 5_500.
        var ev = tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Buy, filledQtyTicks: 100, fillPriceTicks: 6_000);

        var snap = Snap(ev);
        Assert.Equal(200, snap.NetPositionTicks);
        Assert.Equal(5_500, snap.AveragePriceTicks);
    }

    [Fact]
    public void OnFill_FlipsThroughZero_VwapResetsToNewFillPrice()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        // Long 100 @ 5_000.
        tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Buy, filledQtyTicks: 100, fillPriceTicks: 5_000);
        // Sell 250 @ 4_000 → flip to short 150; VWAP resets to 4_000.
        var ev = tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Sell, filledQtyTicks: 250, fillPriceTicks: 4_000);

        var snap = Snap(ev);
        Assert.Equal(-150, snap.NetPositionTicks);
        Assert.Equal(4_000, snap.AveragePriceTicks);
    }

    [Fact]
    public void OnFill_PartialClose_VwapUnchanged()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        // Long 200 @ 5_000.
        tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Buy, filledQtyTicks: 200, fillPriceTicks: 5_000);
        // Sell 100 @ 6_000 → still long 100; VWAP unchanged.
        var ev = tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Sell, filledQtyTicks: 100, fillPriceTicks: 6_000);

        var snap = Snap(ev);
        Assert.Equal(100, snap.NetPositionTicks);
        Assert.Equal(5_000, snap.AveragePriceTicks);
    }

    [Fact]
    public void OnFill_FullClose_VwapZerosOut()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Buy, filledQtyTicks: 100, fillPriceTicks: 5_000);
        var ev = tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
            MarketProto.Side.Sell, filledQtyTicks: 100, fillPriceTicks: 6_000);

        var snap = Snap(ev);
        Assert.Equal(0, snap.NetPositionTicks);
        Assert.Equal(0, snap.AveragePriceTicks);
    }

    [Fact]
    public void OnFill_UnknownInstrument_Throws()
    {
        var state = NewState();
        var tracker = new PositionTracker();
        var dto = Dto("H1");

        Assert.Throws<ArgumentException>(() =>
            tracker.OnFill(state, dto, "ZZ", MarketProto.ProductType.Hour,
                MarketProto.Side.Buy, filledQtyTicks: 1, fillPriceTicks: 1));
    }

    [Fact]
    public void OnFill_NonPositiveQty_Throws()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
                MarketProto.Side.Buy, filledQtyTicks: 0, fillPriceTicks: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
                MarketProto.Side.Buy, filledQtyTicks: -10, fillPriceTicks: 1));
    }

    [Fact]
    public void OnFill_UnspecifiedSide_Throws()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        Assert.Throws<ArgumentException>(() =>
            tracker.OnFill(state, Dto("H1"), "H1", MarketProto.ProductType.Hour,
                MarketProto.Side.Unspecified, filledQtyTicks: 1, fillPriceTicks: 1));
    }

    [Fact]
    public void OnOrderAccepted_AddsToOpenOrdersAndIncrementsNotional()
    {
        var state = NewState();
        var tracker = new PositionTracker();
        var record = new OpenOrder(
            OrderId: 42, ClientOrderId: "co-1", InstrumentIndex: 0, Side: "Buy",
            PriceTicks: 5_000, QuantityTicks: 100, DisplaySliceTicks: 0,
            SubmittedAtUtc: DateTimeOffset.UnixEpoch);

        tracker.OnOrderAccepted(state, record);

        Assert.Single(state.OpenOrdersByInstrument[0]);
        Assert.Equal(42, state.OpenOrdersByInstrument[0][0].OrderId);
        Assert.Equal(500_000, state.OpenOrdersNotionalTicks[0]);
    }

    [Fact]
    public void OnOrderCancelled_RemovesAndDecrementsNotional()
    {
        var state = NewState();
        var tracker = new PositionTracker();
        var record = new OpenOrder(
            OrderId: 42, ClientOrderId: "co-1", InstrumentIndex: 0, Side: "Buy",
            PriceTicks: 5_000, QuantityTicks: 100, DisplaySliceTicks: 0,
            SubmittedAtUtc: DateTimeOffset.UnixEpoch);
        tracker.OnOrderAccepted(state, record);

        tracker.OnOrderCancelled(state, instrumentIndex: 0, orderId: 42);

        Assert.Empty(state.OpenOrdersByInstrument[0]);
        Assert.Equal(0, state.OpenOrdersNotionalTicks[0]);
    }

    [Fact]
    public void OnOrderCancelled_UnknownOrderId_NoOp()
    {
        var state = NewState();
        var tracker = new PositionTracker();

        tracker.OnOrderCancelled(state, instrumentIndex: 0, orderId: 999);

        Assert.Empty(state.OpenOrdersByInstrument[0]);
        Assert.Equal(0, state.OpenOrdersNotionalTicks[0]);
    }

    [Fact]
    public void OnOrderReplaced_SwapsRecordAndAdjustsNotional()
    {
        var state = NewState();
        var tracker = new PositionTracker();
        var original = new OpenOrder(
            OrderId: 42, ClientOrderId: "co-1", InstrumentIndex: 0, Side: "Buy",
            PriceTicks: 5_000, QuantityTicks: 100, DisplaySliceTicks: 0,
            SubmittedAtUtc: DateTimeOffset.UnixEpoch);
        tracker.OnOrderAccepted(state, original);
        var replacement = new OpenOrder(
            OrderId: 43, ClientOrderId: "co-1", InstrumentIndex: 0, Side: "Buy",
            PriceTicks: 6_000, QuantityTicks: 200, DisplaySliceTicks: 0,
            SubmittedAtUtc: DateTimeOffset.UnixEpoch);

        tracker.OnOrderReplaced(state, instrumentIndex: 0, oldOrderId: 42, newRecord: replacement);

        Assert.Single(state.OpenOrdersByInstrument[0]);
        Assert.Equal(43, state.OpenOrdersByInstrument[0][0].OrderId);
        // 200 * 6_000 = 1_200_000.
        Assert.Equal(1_200_000, state.OpenOrdersNotionalTicks[0]);
    }

    [Fact]
    public void OnPartialOrFullFill_Partial_DecrementsNotionalAndShrinksRecord()
    {
        var state = NewState();
        var tracker = new PositionTracker();
        var record = new OpenOrder(
            OrderId: 42, ClientOrderId: "co-1", InstrumentIndex: 0, Side: "Buy",
            PriceTicks: 5_000, QuantityTicks: 100, DisplaySliceTicks: 0,
            SubmittedAtUtc: DateTimeOffset.UnixEpoch);
        tracker.OnOrderAccepted(state, record);

        tracker.OnPartialOrFullFill(state, instrumentIndex: 0, orderId: 42, filledQtyTicks: 30);

        Assert.Single(state.OpenOrdersByInstrument[0]);
        Assert.Equal(70, state.OpenOrdersByInstrument[0][0].QuantityTicks);
        // 100 * 5000 - 30 * 5000 = 350_000.
        Assert.Equal(350_000, state.OpenOrdersNotionalTicks[0]);
    }

    [Fact]
    public void OnPartialOrFullFill_Full_RemovesRecord()
    {
        var state = NewState();
        var tracker = new PositionTracker();
        var record = new OpenOrder(
            OrderId: 42, ClientOrderId: "co-1", InstrumentIndex: 0, Side: "Buy",
            PriceTicks: 5_000, QuantityTicks: 100, DisplaySliceTicks: 0,
            SubmittedAtUtc: DateTimeOffset.UnixEpoch);
        tracker.OnOrderAccepted(state, record);

        tracker.OnPartialOrFullFill(state, instrumentIndex: 0, orderId: 42, filledQtyTicks: 100);

        Assert.Empty(state.OpenOrdersByInstrument[0]);
        Assert.Equal(0, state.OpenOrdersNotionalTicks[0]);
    }

    [Fact]
    public void SnapshotAll_Returns5Snapshots_OneEachInstrument_DeterministicOrder()
    {
        var state = NewState();
        var tracker = new PositionTracker();
        // Set H1 net = 10, Q3 net = -5 so we can prove order.
        state.NetPositionTicks[0] = 10;
        state.VwapTicks[0] = 1_000;
        state.NetPositionTicks[3] = -5;
        state.VwapTicks[3] = 2_000;

        var snaps = tracker.SnapshotAll(state,
            id => Dto(id),
            id => id == "H1" ? MarketProto.ProductType.Hour : MarketProto.ProductType.Quarter);

        Assert.Equal(5, snaps.Length);
        Assert.Equal(10, snaps[0].PositionSnapshot.NetPositionTicks);
        Assert.Equal(1_000, snaps[0].PositionSnapshot.AveragePriceTicks);
        Assert.Equal(0, snaps[1].PositionSnapshot.NetPositionTicks);
        Assert.Equal(0, snaps[2].PositionSnapshot.NetPositionTicks);
        Assert.Equal(-5, snaps[3].PositionSnapshot.NetPositionTicks);
        Assert.Equal(0, snaps[4].PositionSnapshot.NetPositionTicks);
        Assert.Equal(MarketProto.ProductType.Hour, snaps[0].PositionSnapshot.Instrument.ProductType);
        Assert.Equal(MarketProto.ProductType.Quarter, snaps[3].PositionSnapshot.Instrument.ProductType);
    }
}
