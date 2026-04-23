using Bifrost.Exchange.Domain;

namespace Bifrost.Quoter.Pricing;

/// <summary>
/// Pure-math classic microprice with quoter-self-exclusion.
/// <para>
/// Formula: <c>(askQty · bestBid + bidQty · bestAsk) / (askQty + bidQty)</c>, rounded to
/// the nearest tick. Returns <c>null</c> when either side is empty, one-sided, or becomes
/// empty after filtering out the quoter-owned orders supplied by
/// <c>quoterOwnedOrderIds</c>. The caller is expected to fall back to a pure-truth
/// fair-value blend (i.e. weight w = 1.0) when <c>null</c> is returned.
/// </para>
/// <para>
/// BookView shape note: this implementation walks <see cref="OrderBook"/> directly because
/// the exchange domain exposes <see cref="OrderBook.Bids"/> / <see cref="OrderBook.Asks"/>
/// as <see cref="IReadOnlyDictionary{TKey,TValue}"/> of <see cref="Price"/> to
/// <see cref="PriceLevel"/>, with each <see cref="PriceLevel"/> exposing its individual
/// <see cref="Order"/> instances via <see cref="PriceLevel.Orders"/>. This permits
/// per-order filtering (Strategy A): we walk levels best-first, summing visible quantity
/// from non-quoter orders only, and skip levels whose entire visible quantity belongs to
/// the quoter.
/// </para>
/// </summary>
public static class MicropriceCalculator
{
    /// <summary>
    /// Computes the classic microprice on a snapshot of <paramref name="book"/> with
    /// the orders in <paramref name="quoterOwnedOrderIds"/> excluded from both sides.
    /// </summary>
    /// <param name="book">Order book snapshot to read best-of-book from.</param>
    /// <param name="quoterOwnedOrderIds">
    /// The set of <see cref="OrderId"/> values the quoter currently owns at any level on
    /// any side, typically obtained from
    /// <c>PyramidQuoteTracker.GetAllOrderIds(instrument)</c>. Orders with these ids are
    /// subtracted from each side's best-of-book quantity; if removing them empties the
    /// best level, the next level becomes best.
    /// </param>
    /// <returns>The microprice in ticks, or <c>null</c> when either side has no
    /// non-quoter visible quantity.</returns>
    public static long? Compute(OrderBook book, IReadOnlySet<OrderId> quoterOwnedOrderIds)
    {
        if (!TryGetBestNonSelf(book, Side.Buy, quoterOwnedOrderIds, out var bestBidTicks, out var bidsTotalQty))
            return null;

        if (!TryGetBestNonSelf(book, Side.Sell, quoterOwnedOrderIds, out var bestAskTicks, out var asksTotalQty))
            return null;

        var denominator = (double)(asksTotalQty + bidsTotalQty);
        if (denominator <= 0.0)
            return null;

        var numerator = (double)asksTotalQty * bestBidTicks + (double)bidsTotalQty * bestAskTicks;
        return (long)Math.Round(numerator / denominator);
    }

    private static bool TryGetBestNonSelf(
        OrderBook book,
        Side side,
        IReadOnlySet<OrderId> quoterOwnedOrderIds,
        out long bestPriceTicks,
        out decimal totalQuantity)
    {
        // Iteration order matches OrderBook's sort: descending price for Bids,
        // ascending for Asks. The first level whose non-self visible quantity is
        // strictly positive is the best non-self level.
        foreach (var level in book.GetLevels(side))
        {
            var nonSelfQty = SumNonSelfDisplayedQuantity(level, quoterOwnedOrderIds);
            if (nonSelfQty > 0m)
            {
                bestPriceTicks = level.Price.Ticks;
                totalQuantity = nonSelfQty;
                return true;
            }
        }

        bestPriceTicks = 0;
        totalQuantity = 0m;
        return false;
    }

    private static decimal SumNonSelfDisplayedQuantity(PriceLevel level, IReadOnlySet<OrderId> quoterOwnedOrderIds)
    {
        var total = 0m;
        foreach (var order in level.Orders)
        {
            if (quoterOwnedOrderIds.Contains(order.OrderId))
                continue;

            total += order.DisplayedQuantity.Value;
        }

        return total;
    }
}
