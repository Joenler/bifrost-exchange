using Bifrost.Quoter.Pricing;
using Xunit;

namespace Bifrost.Quoter.Tests.Pricing;

/// <summary>
/// Pure-math tests for <see cref="HardCapGuard"/>. Asserts the no-skew, one-sided
/// suppression policy and the hysteresis release behaviour. The cap parameters used
/// throughout the table are the configured defaults
/// (<c>MaxNetPosition = 100</c>, <c>HardCapRelease = 80</c>) so the test names line up
/// with the operational scenarios.
/// </summary>
public sealed class HardCapGuardTests
{
    private const decimal Max = 100m;
    private const decimal Release = 80m;

    private static readonly InventoryDirective Both = new(QuoteBids: true, QuoteAsks: true);
    private static readonly InventoryDirective AsksSuppressed = new(QuoteBids: true, QuoteAsks: false);
    private static readonly InventoryDirective BidsSuppressed = new(QuoteBids: false, QuoteAsks: true);

    [Fact]
    public void Evaluate_NetAboveMax_SuppressesAsks()
    {
        var result = HardCapGuard.Evaluate(netPosition: 101m, Max, Release, previous: Both);
        Assert.Equal(AsksSuppressed, result);
    }

    [Fact]
    public void Evaluate_NetBelowNegativeMax_SuppressesBids()
    {
        var result = HardCapGuard.Evaluate(netPosition: -101m, Max, Release, previous: Both);
        Assert.Equal(BidsSuppressed, result);
    }

    [Theory]
    [InlineData(81)]
    [InlineData(90)]
    [InlineData(100)]
    public void Evaluate_NetInsideHysteresis_AfterAskSuppression_PreservesAskSuppression(int net)
    {
        var result = HardCapGuard.Evaluate(netPosition: net, Max, Release, previous: AsksSuppressed);
        Assert.Equal(AsksSuppressed, result);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(50)]
    [InlineData(0)]
    public void Evaluate_NetAtOrBelowRelease_AfterAskSuppression_ReleasesBothSides(int net)
    {
        var result = HardCapGuard.Evaluate(netPosition: net, Max, Release, previous: AsksSuppressed);
        Assert.Equal(Both, result);
    }

    [Theory]
    [InlineData(-81)]
    [InlineData(-90)]
    [InlineData(-100)]
    public void Evaluate_NetInsideNegativeHysteresis_AfterBidSuppression_PreservesBidSuppression(int net)
    {
        var result = HardCapGuard.Evaluate(netPosition: net, Max, Release, previous: BidsSuppressed);
        Assert.Equal(BidsSuppressed, result);
    }

    [Theory]
    [InlineData(-80)]
    [InlineData(-50)]
    [InlineData(0)]
    public void Evaluate_NetAtOrAboveNegativeRelease_AfterBidSuppression_ReleasesBothSides(int net)
    {
        var result = HardCapGuard.Evaluate(netPosition: net, Max, Release, previous: BidsSuppressed);
        Assert.Equal(Both, result);
    }

    [Theory]
    [InlineData(true,  true)]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public void Evaluate_ActivationTakesPrecedenceOverHysteresis_PositiveBreach(bool prevBids, bool prevAsks)
    {
        var prev = new InventoryDirective(prevBids, prevAsks);
        var result = HardCapGuard.Evaluate(netPosition: 150m, Max, Release, previous: prev);
        Assert.Equal(AsksSuppressed, result);
    }

    [Theory]
    [InlineData(true,  true)]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public void Evaluate_ActivationTakesPrecedenceOverHysteresis_NegativeBreach(bool prevBids, bool prevAsks)
    {
        var prev = new InventoryDirective(prevBids, prevAsks);
        var result = HardCapGuard.Evaluate(netPosition: -150m, Max, Release, previous: prev);
        Assert.Equal(BidsSuppressed, result);
    }
}
