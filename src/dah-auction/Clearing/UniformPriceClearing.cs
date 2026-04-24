// UniformPriceClearing.cs
//
// Computes the Day-Ahead uniform-price pay-as-cleared clearing for a single
// quarter-hour (QH). Each team submits a "bid matrix" per QH with a list of
// marginal step orders: buy_steps (strictly descending in price, each willing
// to buy that quantity at or BELOW the step price) and sell_steps (strictly
// ascending in price, each willing to sell that quantity at or ABOVE the step
// price). Prices may be negative (real-world DAH convention — Nordic/CE
// markets see negative prices during renewable-surplus hours).
//
// Clearing algorithm (textbook EUPHEMIA):
//
//   1. Merge all teams' buy_steps into one flat list; sort DESCENDING by
//      price_ticks. Ties: break by team_name ordinal ascending, then step
//      index ascending. This yields the aggregate DEMAND curve as a
//      decreasing step function D(p): the quantity buyers collectively want
//      at price <= p.
//   2. Merge all teams' sell_steps into one flat list; sort ASCENDING by
//      price_ticks. Same tie-break. Yields aggregate SUPPLY curve S(p):
//      quantity sellers collectively offer at price >= p.
//   3. Walk both lists simultaneously, accumulating cumulative demand and
//      cumulative supply at each distinct price crossing point.
//   4. The CLEARING PRICE p* is the lowest price where S(p*) >= D(p*) with
//      positive volume on both sides. Every awarded unit clears at p*
//      (this is the "uniform price" / "pay-as-cleared" rule: regardless of
//      what a team bid, they pay or receive p* for each cleared unit).
//   5. All steps "in the money" (buy prices >= p*, sell prices <= p*) fill
//      fully, except the MARGINAL step on the short side, which partial-
//      fills pro rata across all steps at that marginal price — see
//      AllocateSide() for the deterministic remainder-rounding rule.
//
// Reference: EUPHEMIA Public Description §3 "Stepwise curves and aggregation"
// https://www.nemo-committee.eu/assets/files/euphemia-public-description.pdf
//
// Determinism contract: given the same bid inputs, this function produces a
// byte-identical ClearingResult for each QH across runs. No randomness, no
// clock, no culture-dependent parsing; all comparisons are ordinal; all
// rounding is deterministic (floor-with-pro-rata-remainder).

using Bifrost.Contracts.Internal.Auction;

namespace Bifrost.DahAuction.Clearing;

/// <summary>
/// Pure static uniform-price pay-as-cleared clearing engine. The textbook
/// EUPHEMIA merged step-curve scan applied to a single quarter-hour: aggregate
/// demand (buy-side) descending, aggregate supply (sell-side) ascending, find
/// the lowest crossing price, allocate awards (full fills + pro-rata margin).
/// </summary>
public static class UniformPriceClearing
{
    // Internal record: a step tagged with its team and submission order so the
    // tie-break rule (team_name ordinal, step index) has the data it needs.
    private sealed record TaggedStep(string Team, int Index, long PriceTicks, long QuantityTicks);

    /// <summary>
    /// Clear a single quarter-hour against the aggregate demand and supply
    /// curves implied by <paramref name="bids"/>. Returns either
    /// <see cref="ClearingOutcome.Cleared(string, long, System.Collections.Generic.IReadOnlyList{System.ValueTuple{string, long}})"/>
    /// (positive crossing volume) or
    /// <see cref="ClearingOutcome.NoCross(string)"/> (curves do not cross,
    /// or either side is empty).
    /// </summary>
    /// <remarks>
    /// Textbook EUPHEMIA merged step-curve scan; see file header for a full
    /// walk-through and the EUPHEMIA Public Description URL:
    /// https://www.nemo-committee.eu/assets/files/euphemia-public-description.pdf.
    ///
    /// Deterministic tie-break: ordinal team_name ascending, then step index
    /// ascending. Same rule applied at aggregation (stages 1 + 2) and at
    /// pro-rata allocation (stage 5).
    /// </remarks>
    public static ClearingOutcome Compute(string quarterId, IReadOnlyList<BidMatrixDto> bids)
    {
        // Stage 1 — aggregate DEMAND: merge all teams' buy_steps into one flat
        // list, sort descending by price (ties: team_name ordinal, step index).
        var aggregateDemand = AggregateSteps(bids, isBuy: true);

        // Stage 2 — aggregate SUPPLY: same for sell_steps, sorted ascending.
        var aggregateSupply = AggregateSteps(bids, isBuy: false);

        // Short-circuit: either side empty => no cross.
        if (aggregateDemand.Count == 0 || aggregateSupply.Count == 0)
        {
            return ClearingOutcome.NoCross(quarterId);
        }

        // Stage 3 — walk both curves to find the crossing price. At each
        // distinct price, we know cumulative demand up to that price (sum of
        // buy quantities at price >= this) and cumulative supply (sum of sell
        // quantities at price <= this). The crossing is where supply catches up
        // to demand with POSITIVE volume on both sides.
        var crossing = FindCrossingPrice(aggregateDemand, aggregateSupply);
        if (crossing is null)
        {
            return ClearingOutcome.NoCross(quarterId);
        }
        long pStar = crossing.Value;

        // Stage 4 — identify the short side and the matched volume at p*. The
        // short side is whichever curve has less in-the-money cumulative
        // quantity at p* (buy if demand < supply at p*; sell otherwise). The
        // matched volume is the min of the two cumulative totals.
        long cumulativeDemandAtPStar = aggregateDemand.Where(s => s.PriceTicks >= pStar).Sum(s => s.QuantityTicks);
        long cumulativeSupplyAtPStar = aggregateSupply.Where(s => s.PriceTicks <= pStar).Sum(s => s.QuantityTicks);
        long matchedVolume = Math.Min(cumulativeDemandAtPStar, cumulativeSupplyAtPStar);

        if (matchedVolume == 0)
        {
            return ClearingOutcome.NoCross(quarterId);
        }

        // Stage 5 — allocate awards. In-the-money infra-marginal steps fill
        // fully at p*; the marginal step(s) on the short side partial-fill
        // pro-rata (floor division with deterministic single-tick remainder
        // distribution in team ordinal / step-index order).
        var perTeamAwards = AllocateAwards(pStar, aggregateDemand, aggregateSupply, matchedVolume);
        return ClearingOutcome.Cleared(quarterId, pStar, perTeamAwards);
    }

    // Aggregation helper — stage 1 / 2.
    private static List<TaggedStep> AggregateSteps(IReadOnlyList<BidMatrixDto> bids, bool isBuy)
    {
        var flat = new List<TaggedStep>();
        foreach (var bm in bids.OrderBy(b => b.TeamName, StringComparer.Ordinal))
        {
            var steps = isBuy ? bm.BuySteps : bm.SellSteps;
            for (int i = 0; i < steps.Length; i++)
            {
                flat.Add(new TaggedStep(bm.TeamName, i, steps[i].PriceTicks, steps[i].QuantityTicks));
            }
        }
        // isBuy => descending by price; isSell => ascending. Ties broken by
        // team ordinal then step index (already in ascending order from the
        // outer OrderBy + inner index loop; OrderBy is stable in LINQ so the
        // ThenBy on Index preserves ordering).
        return isBuy
            ? flat.OrderByDescending(s => s.PriceTicks)
                  .ThenBy(s => s.Team, StringComparer.Ordinal)
                  .ThenBy(s => s.Index)
                  .ToList()
            : flat.OrderBy(s => s.PriceTicks)
                  .ThenBy(s => s.Team, StringComparer.Ordinal)
                  .ThenBy(s => s.Index)
                  .ToList();
    }

    // Crossing search — stage 3 + 4. Returns the lowest price where S(p) >= D(p)
    // with positive matched volume. Candidate prices are the union of distinct
    // prices on both curves; iterating in ascending order is enough to find the
    // lowest crossing.
    private static long? FindCrossingPrice(List<TaggedStep> demand, List<TaggedStep> supply)
    {
        // Build the candidate-price set: every price that appears on either
        // curve. Sort ascending (we want the LOWEST crossing price).
        var candidates = demand.Select(s => s.PriceTicks)
            .Concat(supply.Select(s => s.PriceTicks))
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        foreach (var p in candidates)
        {
            // Demand at price p: sum of buy steps whose price >= p.
            long d = demand.Where(s => s.PriceTicks >= p).Sum(s => s.QuantityTicks);
            // Supply at price p: sum of sell steps whose price <= p.
            long s = supply.Where(sv => sv.PriceTicks <= p).Sum(sv => sv.QuantityTicks);
            if (s >= d && d > 0)
            {
                return p;
            }
        }
        return null;
    }

    // Award allocation — stage 5 with pro-rata allocation at the marginal price.
    private static IReadOnlyList<(string TeamName, long AwardedQuantityTicks)> AllocateAwards(
        long pStar,
        List<TaggedStep> demand,
        List<TaggedStep> supply,
        long matchedVolume)
    {
        // Filter to the in-the-money steps on each side (infra-marginal + marginal).
        var buyInMoney = demand.Where(s => s.PriceTicks >= pStar).ToList();
        var sellInMoney = supply.Where(s => s.PriceTicks <= pStar).ToList();

        // Allocate per-side quantities. On the short side every step fills fully.
        // On the long side, infra-marginal (strictly better than margin) fills
        // fully; at the marginal price tier, steps partial-fill pro-rata.
        var buyAwardsPerStep = AllocateSide(buyInMoney, matchedVolume);   // buys are positive
        var sellAwardsPerStep = AllocateSide(sellInMoney, matchedVolume); // sell will be negated below

        // Roll up by team_name, signing sells negative.
        var byTeam = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (step, qty) in buyAwardsPerStep)
        {
            byTeam[step.Team] = byTeam.GetValueOrDefault(step.Team, 0L) + qty;
        }
        foreach (var (step, qty) in sellAwardsPerStep)
        {
            byTeam[step.Team] = byTeam.GetValueOrDefault(step.Team, 0L) - qty;
        }

        // Emit in team_name ordinal order for determinism, omitting teams that
        // net to zero (no DAH position).
        return byTeam
            .Where(kv => kv.Value != 0L)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    // Per-side allocation: infra-marginal steps fill fully; marginal steps
    // pro-rata (floor division + one-tick remainder distribution in team
    // ordinal, step index order).
    private static List<(TaggedStep Step, long Awarded)> AllocateSide(List<TaggedStep> inMoney, long matchedVolume)
    {
        long sideTotal = inMoney.Sum(s => s.QuantityTicks);
        if (sideTotal <= matchedVolume)
        {
            // Short side: every step fills fully.
            return inMoney.Select(s => (s, s.QuantityTicks)).ToList();
        }

        // Long side: allocate greedily in curve order until we'd exceed matched
        // volume; the step that would tip us over is the margin price; all
        // steps at that same price share the remainder via pro-rata.
        var awards = new List<(TaggedStep Step, long Awarded)>();
        long remaining = matchedVolume;
        int i = 0;
        while (i < inMoney.Count && remaining > 0)
        {
            // Find the full tier at this price (possibly multiple steps).
            int tierStart = i;
            long tierPrice = inMoney[i].PriceTicks;
            long tierTotal = 0L;
            while (i < inMoney.Count && inMoney[i].PriceTicks == tierPrice)
            {
                tierTotal += inMoney[i].QuantityTicks;
                i++;
            }
            int tierEnd = i; // exclusive

            if (tierTotal <= remaining)
            {
                // Tier fills fully.
                for (int k = tierStart; k < tierEnd; k++)
                {
                    awards.Add((inMoney[k], inMoney[k].QuantityTicks));
                }
                remaining -= tierTotal;
            }
            else
            {
                // Partial-fill marginal tier pro-rata. Sort the marginal-tier
                // steps by team_name ordinal then step index so the floor
                // division and remainder distribution are deterministic.
                var marginSteps = inMoney.GetRange(tierStart, tierEnd - tierStart)
                    .OrderBy(s => s.Team, StringComparer.Ordinal)
                    .ThenBy(s => s.Index)
                    .ToList();
                long residual = remaining;
                long totalMarginQty = marginSteps.Sum(s => s.QuantityTicks);

                // Floor-divide share per step.
                var shares = marginSteps
                    .Select(s => (Step: s, Share: residual * s.QuantityTicks / totalMarginQty))
                    .ToList();
                long allocated = shares.Sum(x => x.Share);
                long remainder = residual - allocated;

                // Distribute remainder one tick at a time in the same sort
                // order until residual is exhausted. By floor-math invariant
                // remainder < marginSteps.Count, so a single pass suffices.
                var final = shares.Select(x => (x.Step, Awarded: x.Share)).ToList();
                int idx = 0;
                while (remainder > 0 && idx < final.Count)
                {
                    final[idx] = (final[idx].Step, final[idx].Awarded + 1L);
                    remainder--;
                    idx++;
                }
                awards.AddRange(final);
                remaining = 0;
            }
        }
        return awards;
    }
}
