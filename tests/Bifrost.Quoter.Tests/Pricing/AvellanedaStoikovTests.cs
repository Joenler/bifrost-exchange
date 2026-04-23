using Bifrost.Quoter.Pricing;
using Xunit;

namespace Bifrost.Quoter.Tests.Pricing;

/// <summary>
/// Pure-math tests for AvellanedaStoikov. See UPSTREAM.md for Arena provenance
/// and the assertion-style adaptation (FluentAssertions -> plain xUnit Assert).
/// Tests touching pricing-config types out of donation scope are dropped per
/// the math-only triage rule.
/// </summary>
public class AvellanedaStoikovTests
{
    // --- ReservationPrice ---

    [Fact]
    public void ReservationPrice_LongInventory_LowersReservation()
    {
        var result = AvellanedaStoikov.ReservationPrice(100.0, 5.0, 0.1, 0.05, 1.0);

        // midPrice - netPos * inventoryRiskAversion * priceVolatility^2 * timeToDeliveryHours = 100 - 5 * 0.1 * 0.0025 * 1 = 99.99875
        Assert.Equal(99.99875, result, 1e-10);
    }

    [Fact]
    public void ReservationPrice_ShortInventory_RaisesReservation()
    {
        var result = AvellanedaStoikov.ReservationPrice(100.0, -5.0, 0.1, 0.05, 1.0);

        Assert.Equal(100.00125, result, 1e-10);
    }

    [Fact]
    public void ReservationPrice_FlatInventory_NoSkew()
    {
        var result = AvellanedaStoikov.ReservationPrice(100.0, 0.0, 0.1, 0.05, 1.0);

        Assert.Equal(100.0, result);
    }

    // --- OptimalHalfSpread ---

    [Fact]
    public void OptimalHalfSpread_StandardParams_ReturnsPositive()
    {
        var result = AvellanedaStoikov.OptimalHalfSpread(0.1, 1.5);

        // (1/inventoryRiskAversion) * ln(1 + inventoryRiskAversion/orderArrivalIntensity) = 10 * ln(1 + 0.0667) = 10 * 0.06454 ~ 0.6454
        Assert.Equal(0.6454, result, 0.001);
    }

    [Fact]
    public void OptimalHalfSpread_HigherOrderArrivalIntensity_NarrowerSpread()
    {
        var lowArrivalIntensity = AvellanedaStoikov.OptimalHalfSpread(0.1, 1.5);
        var highArrivalIntensity = AvellanedaStoikov.OptimalHalfSpread(0.1, 3.0);

        Assert.True(lowArrivalIntensity > highArrivalIntensity);
    }

    // --- QuotableRange ---

    [Fact]
    public void ComputeQuotableRange_StandardSpread_BidBelowFairValueAskAbove()
    {
        var range = AvellanedaStoikov.ComputeQuotableRange(500, 3.0, 1);

        Assert.Equal(497, range.BidTicks);
        Assert.Equal(503, range.AskTicks);
        Assert.True(range.BidTicks < 500);
        Assert.True(range.AskTicks > 500);
    }

    [Fact]
    public void ComputeQuotableRange_TinySpread_StillMaintainsSeparation()
    {
        var range = AvellanedaStoikov.ComputeQuotableRange(500, 0.5, 1);

        Assert.True(range.BidTicks < 500);
        Assert.True(range.AskTicks > 500);
    }

    // --- HittableRange ---

    [Fact]
    public void ComputeHittableRange_WiderThanQuotable()
    {
        var quotable = AvellanedaStoikov.ComputeQuotableRange(500, 3.0, 1);
        var hittable = AvellanedaStoikov.ComputeHittableRange(500, 3.0, 1.5, 1);

        Assert.True(hittable.BidTicks <= quotable.BidTicks);
        Assert.True(hittable.AskTicks >= quotable.AskTicks);
    }

    [Fact]
    public void ComputeHittableRange_FairValueStrictlyInside()
    {
        var range = AvellanedaStoikov.ComputeHittableRange(500, 3.0, 1.5, 1);

        Assert.True(range.BidTicks < 500);
        Assert.True(range.AskTicks > 500);
    }

    // --- SideBias ---

    [Fact]
    public void ComputeSideBias_LongPosition_ReturnsShort()
    {
        Assert.Equal(SideBias.Short, AvellanedaStoikov.ComputeSideBias(5.0));
    }

    [Fact]
    public void ComputeSideBias_ShortPosition_ReturnsLong()
    {
        Assert.Equal(SideBias.Long, AvellanedaStoikov.ComputeSideBias(-3.0));
    }

    [Fact]
    public void ComputeSideBias_FlatPosition_ReturnsNeutral()
    {
        Assert.Equal(SideBias.Neutral, AvellanedaStoikov.ComputeSideBias(0.0));
    }
}
