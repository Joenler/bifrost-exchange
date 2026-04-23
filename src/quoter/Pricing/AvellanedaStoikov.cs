namespace Bifrost.Quoter.Pricing;

/// <summary>
/// Pure math functions implementing the Avellaneda-Stoikov market-making model.
/// All methods are stateless and deterministic.
/// </summary>
public static class AvellanedaStoikov
{
    /// <summary>
    /// Computes the inventory-adjusted reservation price.
    /// Long inventory lowers the reservation price (willing to sell cheaper),
    /// short inventory raises it (willing to buy higher).
    /// </summary>
    /// <remarks>
    /// Avellaneda, M. and Stoikov, S. (2008) Eq. 10: r = s - q * gamma * sigma^2 * tau
    /// </remarks>
    public static double ReservationPrice(
        double midPrice,
        double netPosition,
        double inventoryRiskAversion,
        double priceVolatility,
        double timeToDeliveryHours)
    {
        // A-S (2008) Eq. 10: r = s - q*gamma*sigma^2*tau
        return midPrice - netPosition * inventoryRiskAversion * priceVolatility * priceVolatility * timeToDeliveryHours;
    }

    /// <summary>
    /// Computes the optimal half-spread from the GLFT asymptotic solution.
    /// This is the second term of the full A-S spread formula.
    /// Higher orderArrivalIntensity narrows the spread.
    /// </summary>
    /// <remarks>
    /// Gueant, Lehalle, Fernandez-Tapia (2013) asymptotic solution, Eq. 4: delta = (1/gamma) * ln(1 + gamma/kappa)
    /// </remarks>
    public static double OptimalHalfSpread(double inventoryRiskAversion, double orderArrivalIntensity)
    {
        // GLFT (2013) Eq. 4: delta = (1/gamma) * ln(1 + gamma/kappa)
        return (1.0 / inventoryRiskAversion) * Math.Log(1.0 + inventoryRiskAversion / orderArrivalIntensity);
    }

    /// <summary>
    /// Computes tick-aligned quotable range centered on fair value.
    /// Bid is floored, ask is ceiled. Guards ensure strict separation from fair value.
    /// </summary>
    public static QuotableRange ComputeQuotableRange(
        long fairValueTicks,
        double halfSpread,
        long tickSize = 1)
    {
        var rawBid = fairValueTicks - halfSpread;
        var rawAsk = fairValueTicks + halfSpread;

        var bid = FloorToTick(rawBid, tickSize);
        var ask = CeilToTick(rawAsk, tickSize);

        if (bid >= fairValueTicks)
            bid -= tickSize;
        if (ask <= fairValueTicks)
            ask += tickSize;

        return new QuotableRange(bid, ask);
    }

    /// <summary>
    /// Computes tick-aligned hittable range, wider than quotable by the multiplier factor.
    /// </summary>
    public static HittableRange ComputeHittableRange(
        long fairValueTicks,
        double halfSpread,
        double hittableMultiplier,
        long tickSize = 1)
    {
        var widenedHalfSpread = halfSpread * hittableMultiplier;
        var rawBid = fairValueTicks - widenedHalfSpread;
        var rawAsk = fairValueTicks + widenedHalfSpread;

        var bid = FloorToTick(rawBid, tickSize);
        var ask = CeilToTick(rawAsk, tickSize);

        if (bid >= fairValueTicks)
            bid -= tickSize;
        if (ask <= fairValueTicks)
            ask += tickSize;

        return new HittableRange(bid, ask);
    }

    /// <summary>
    /// Maps net position to trading bias direction.
    /// Long inventory -> short bias (want to reduce), short inventory -> long bias.
    /// </summary>
    public static SideBias ComputeSideBias(double netPosition)
    {
        return netPosition switch
        {
            > 0 => SideBias.Short,
            < 0 => SideBias.Long,
            _ => SideBias.Neutral
        };
    }

    private static long FloorToTick(double value, long tickSize)
    {
        return (long)Math.Floor(value / tickSize) * tickSize;
    }

    private static long CeilToTick(double value, long tickSize)
    {
        return (long)Math.Ceiling(value / tickSize) * tickSize;
    }
}
