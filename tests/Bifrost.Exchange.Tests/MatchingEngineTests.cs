using Bifrost.Exchange.Domain;
using Xunit;

namespace Bifrost.Exchange.Tests;

/// <summary>
/// Baseline coverage for the donated Arena MatchingEngine + OrderBook. Ported from
/// Arena's tests/exchange/Exchange.Domain.Tests/OrderBookTests.cs and adapted to the
/// plain-xUnit / no-FluentAssertions convention established in Phase 00 test projects.
///
/// Covers the price-time-priority invariant (EX-01) and validates the new
/// RejectionCode.ExchangeClosed enum value added in this plan (EX-07).
/// </summary>
public class MatchingEngineTests
{
    // Static test instrument: DE / hour at 2030-01-01T10:00Z (far-future to avoid
    // HasExpired interactions — Domain tests do not depend on clock state).
    private static readonly InstrumentId TestInstrument = new(
        new DeliveryArea("DE"),
        new DeliveryPeriod(
            new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 11, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void PriceTimePriority_MaintainsOrderAcrossSubmissions()
    {
        var book = new OrderBook(TestInstrument);
        var seqGen = new MonotonicSequenceGenerator();
        var engine = new MatchingEngine(book, seqGen);

        // Submit 3 resting buys at same price, different arrival times.
        engine.SubmitOrder(Order.CreateLimit(
            new OrderId(1), new ClientId("A"), TestInstrument,
            Side.Buy, new Price(100), new Quantity(5), seqGen.Next()));
        engine.SubmitOrder(Order.CreateLimit(
            new OrderId(2), new ClientId("B"), TestInstrument,
            Side.Buy, new Price(100), new Quantity(5), seqGen.Next()));
        engine.SubmitOrder(Order.CreateLimit(
            new OrderId(3), new ClientId("C"), TestInstrument,
            Side.Buy, new Price(100), new Quantity(5), seqGen.Next()));

        // Aggressor sell crosses for 15 units — must fill A, B, C in that time order.
        var result = engine.SubmitOrder(Order.CreateMarket(
            new OrderId(4), new ClientId("D"), TestInstrument,
            Side.Sell, new Quantity(15), seqGen.Next()));

        var fills = result.Events.OfType<TradeFilled>().ToList();
        Assert.Equal(3, fills.Count);
        Assert.Equal("A", fills[0].RestingClientId.Value);
        Assert.Equal("B", fills[1].RestingClientId.Value);
        Assert.Equal("C", fills[2].RestingClientId.Value);
    }

    [Fact]
    public void AddBuyOrder_CreatesLevel()
    {
        var book = new OrderBook(TestInstrument);
        var seqGen = new MonotonicSequenceGenerator();
        var order = Order.CreateLimit(
            new OrderId(1), new ClientId("client-a"), TestInstrument,
            Side.Buy, new Price(100), new Quantity(10), seqGen.Next());

        book.AddOrder(order);

        Assert.Equal(new Price(100), book.BestBid);
        Assert.Equal(1, book.TotalOrderCount);
        Assert.Same(order, book.GetOrder(new OrderId(1)));
    }

    [Fact]
    public void AddSellOrder_CreatesLevel()
    {
        var book = new OrderBook(TestInstrument);
        var seqGen = new MonotonicSequenceGenerator();
        var order = Order.CreateLimit(
            new OrderId(1), new ClientId("client-a"), TestInstrument,
            Side.Sell, new Price(105), new Quantity(10), seqGen.Next());

        book.AddOrder(order);

        Assert.Equal(new Price(105), book.BestAsk);
        Assert.Equal(1, book.TotalOrderCount);
    }

    [Fact]
    public void RemoveLastOrder_RemovesLevel()
    {
        var book = new OrderBook(TestInstrument);
        var seqGen = new MonotonicSequenceGenerator();
        var order = Order.CreateLimit(
            new OrderId(1), new ClientId("client-a"), TestInstrument,
            Side.Buy, new Price(100), new Quantity(10), seqGen.Next());

        book.AddOrder(order);
        book.RemoveOrder(order);

        Assert.Null(book.BestBid);
        Assert.Equal(0, book.TotalOrderCount);
        Assert.Null(book.GetOrder(new OrderId(1)));
    }

    [Fact]
    public void MultipleBidLevels_BestBidIsHighest()
    {
        var book = new OrderBook(TestInstrument);
        var seqGen = new MonotonicSequenceGenerator();

        book.AddOrder(Order.CreateLimit(
            new OrderId(1), new ClientId("client-a"), TestInstrument,
            Side.Buy, new Price(98), new Quantity(10), seqGen.Next()));
        book.AddOrder(Order.CreateLimit(
            new OrderId(2), new ClientId("client-a"), TestInstrument,
            Side.Buy, new Price(100), new Quantity(10), seqGen.Next()));
        book.AddOrder(Order.CreateLimit(
            new OrderId(3), new ClientId("client-a"), TestInstrument,
            Side.Buy, new Price(99), new Quantity(10), seqGen.Next()));

        Assert.Equal(new Price(100), book.BestBid);
    }

    [Fact]
    public void MultipleAskLevels_BestAskIsLowest()
    {
        var book = new OrderBook(TestInstrument);
        var seqGen = new MonotonicSequenceGenerator();

        book.AddOrder(Order.CreateLimit(
            new OrderId(1), new ClientId("client-a"), TestInstrument,
            Side.Sell, new Price(102), new Quantity(10), seqGen.Next()));
        book.AddOrder(Order.CreateLimit(
            new OrderId(2), new ClientId("client-a"), TestInstrument,
            Side.Sell, new Price(100), new Quantity(10), seqGen.Next()));
        book.AddOrder(Order.CreateLimit(
            new OrderId(3), new ClientId("client-a"), TestInstrument,
            Side.Sell, new Price(101), new Quantity(10), seqGen.Next()));

        Assert.Equal(new Price(100), book.BestAsk);
    }

    [Fact]
    public void EmptyBook_BestBidAndAskAreNull()
    {
        var book = new OrderBook(TestInstrument);

        Assert.Null(book.BestBid);
        Assert.Null(book.BestAsk);
    }

    [Fact]
    public void GetLevels_ReturnsSortedLevels()
    {
        var book = new OrderBook(TestInstrument);
        var seqGen = new MonotonicSequenceGenerator();

        book.AddOrder(Order.CreateLimit(
            new OrderId(1), new ClientId("client-a"), TestInstrument,
            Side.Sell, new Price(102), new Quantity(10), seqGen.Next()));
        book.AddOrder(Order.CreateLimit(
            new OrderId(2), new ClientId("client-a"), TestInstrument,
            Side.Sell, new Price(100), new Quantity(10), seqGen.Next()));

        var levels = book.GetLevels(Side.Sell).ToList();
        Assert.Equal(2, levels.Count);
        Assert.Equal(new Price(100), levels[0].Price);
        Assert.Equal(new Price(102), levels[1].Price);
    }
}

/// <summary>
/// Sanity tests for the BIFROST-added RejectionCode.ExchangeClosed enum value. Confirms
/// the enum extension, the RejectionCodeNames string constant, and the
/// RejectionCodeExtensions.ToDisplayString() human-readable message are all wired
/// consistently across the three Domain files that were edited in this plan.
/// </summary>
public class RejectionCodeExchangeClosedTests
{
    [Fact]
    public void ExchangeClosed_RejectionCode_IsDefined()
    {
        Assert.True(Enum.IsDefined(typeof(RejectionCode), RejectionCode.ExchangeClosed));
    }

    [Fact]
    public void ExchangeClosed_ToDisplayString_ReturnsGateMessage()
    {
        Assert.Equal("Exchange closed at gate", RejectionCode.ExchangeClosed.ToDisplayString());
    }

    [Fact]
    public void ExchangeClosed_RejectionCodeNamesGet_ReturnsIdentifierString()
    {
        Assert.Equal("ExchangeClosed", RejectionCodeNames.Get(RejectionCode.ExchangeClosed));
    }

    [Fact]
    public void ExchangeClosed_RejectionCodeNamesConstant_MatchesEnumName()
    {
        Assert.Equal("ExchangeClosed", RejectionCodeNames.ExchangeClosed);
    }
}
