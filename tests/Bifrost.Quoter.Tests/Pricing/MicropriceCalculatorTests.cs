using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Pricing;
using Xunit;

namespace Bifrost.Quoter.Tests.Pricing;

/// <summary>
/// Pure-math tests for <see cref="MicropriceCalculator"/>. Asserts the classic-microprice
/// formula on a populated book and the quoter-self-exclusion invariant: when the orders
/// supplied via the filter set strip a side empty, <c>null</c> is returned so the caller
/// can fall back to a pure-truth fair value (weight w = 1.0).
/// </summary>
public sealed class MicropriceCalculatorTests
{
    private static readonly DeliveryArea TestArea = new("DE1");

    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 6, 10, 0, 0, TimeSpan.Zero);

    private static readonly InstrumentId TestInstrument = new(
        TestArea,
        new DeliveryPeriod(BaseTime, BaseTime.AddHours(1)));

    private static long _nextOrderId;
    private static long _nextSeq;

    private static OrderId NextOrderId() => new(Interlocked.Increment(ref _nextOrderId));
    private static SequenceNumber NextSeq() => new(Interlocked.Increment(ref _nextSeq));

    private static Order MakeLimitOrder(Side side, long priceTicks, decimal qty, string clientId = "team-a")
    {
        return Order.CreateLimit(
            NextOrderId(),
            new ClientId(clientId),
            TestInstrument,
            side,
            new Price(priceTicks),
            new Quantity(qty),
            NextSeq());
    }

    [Fact]
    public void Compute_EmptyBook_ReturnsNull()
    {
        var book = new OrderBook(TestInstrument);
        var result = MicropriceCalculator.Compute(book, EmptySet());
        Assert.Null(result);
    }

    [Fact]
    public void Compute_BidsOnly_ReturnsNull()
    {
        var book = new OrderBook(TestInstrument);
        book.AddOrder(MakeLimitOrder(Side.Buy, priceTicks: 100, qty: 5m));
        var result = MicropriceCalculator.Compute(book, EmptySet());
        Assert.Null(result);
    }

    [Fact]
    public void Compute_AsksOnly_ReturnsNull()
    {
        var book = new OrderBook(TestInstrument);
        book.AddOrder(MakeLimitOrder(Side.Sell, priceTicks: 110, qty: 5m));
        var result = MicropriceCalculator.Compute(book, EmptySet());
        Assert.Null(result);
    }

    [Fact]
    public void Compute_TwoSidedBook_NoQuoterOrders_ReturnsClassicMicroprice()
    {
        var book = new OrderBook(TestInstrument);
        book.AddOrder(MakeLimitOrder(Side.Buy, priceTicks: 100, qty: 4m));
        book.AddOrder(MakeLimitOrder(Side.Sell, priceTicks: 110, qty: 6m));

        var result = MicropriceCalculator.Compute(book, EmptySet());

        // microprice = (askQty * bestBid + bidQty * bestAsk) / (askQty + bidQty)
        //            = (6 * 100 + 4 * 110) / (6 + 4)
        //            = (600 + 440) / 10
        //            = 104
        Assert.Equal(104L, result);
    }

    [Fact]
    public void Compute_QuoterAtBestBid_FilterDropsBest_NextLevelUsed()
    {
        var book = new OrderBook(TestInstrument);

        // Quoter sits alone at the best bid, with another team one tick lower.
        var quoterBid = MakeLimitOrder(Side.Buy, priceTicks: 100, qty: 5m, clientId: "quoter");
        var teamBid = MakeLimitOrder(Side.Buy, priceTicks: 99, qty: 3m, clientId: "team-b");
        book.AddOrder(quoterBid);
        book.AddOrder(teamBid);

        // Asks unaffected.
        var teamAsk = MakeLimitOrder(Side.Sell, priceTicks: 110, qty: 6m, clientId: "team-b");
        book.AddOrder(teamAsk);

        var quoterOwned = new HashSet<OrderId> { quoterBid.OrderId };

        var result = MicropriceCalculator.Compute(book, quoterOwned);

        // After filtering: bestBid drops from 100 to 99 (team-b's bid), bidQty = 3.
        // microprice = (askQty * bestBid + bidQty * bestAsk) / (askQty + bidQty)
        //            = (6 * 99 + 3 * 110) / (6 + 3)
        //            = (594 + 330) / 9
        //            = 924 / 9
        //            = 102.6666... → rounded → 103
        Assert.Equal(103L, result);
    }

    [Fact]
    public void Compute_QuoterOwnsEntireBidSide_ReturnsNull()
    {
        var book = new OrderBook(TestInstrument);

        var quoterBid1 = MakeLimitOrder(Side.Buy, priceTicks: 100, qty: 5m, clientId: "quoter");
        var quoterBid2 = MakeLimitOrder(Side.Buy, priceTicks: 99, qty: 4m, clientId: "quoter");
        book.AddOrder(quoterBid1);
        book.AddOrder(quoterBid2);

        var teamAsk = MakeLimitOrder(Side.Sell, priceTicks: 110, qty: 6m);
        book.AddOrder(teamAsk);

        var quoterOwned = new HashSet<OrderId> { quoterBid1.OrderId, quoterBid2.OrderId };

        var result = MicropriceCalculator.Compute(book, quoterOwned);

        // Filter strips bid side empty → caller falls back to pure truth (w = 1.0).
        Assert.Null(result);
    }

    [Fact]
    public void Compute_BestBidSharedByQuoterAndTeam_OnlyQuoterPortionFiltered()
    {
        var book = new OrderBook(TestInstrument);

        // Two orders at the same best bid level: quoter 3 + team 2 → after filter 2 left.
        var quoterBid = MakeLimitOrder(Side.Buy, priceTicks: 100, qty: 3m, clientId: "quoter");
        var teamBid = MakeLimitOrder(Side.Buy, priceTicks: 100, qty: 2m, clientId: "team-c");
        book.AddOrder(quoterBid);
        book.AddOrder(teamBid);

        var teamAsk = MakeLimitOrder(Side.Sell, priceTicks: 110, qty: 4m, clientId: "team-c");
        book.AddOrder(teamAsk);

        var quoterOwned = new HashSet<OrderId> { quoterBid.OrderId };

        var result = MicropriceCalculator.Compute(book, quoterOwned);

        // microprice = (askQty * bestBid + bidQty * bestAsk) / (askQty + bidQty)
        //            = (4 * 100 + 2 * 110) / (4 + 2)
        //            = (400 + 220) / 6
        //            = 620 / 6
        //            = 103.333... → rounded → 103
        Assert.Equal(103L, result);
    }

    private static IReadOnlySet<OrderId> EmptySet() => new HashSet<OrderId>();
}
