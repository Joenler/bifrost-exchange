namespace Bifrost.DahAuction.Clearing;

/// <summary>
/// Result of clearing a single quarter-hour. <see cref="DidCross"/>
/// distinguishes a successful cross from a no-cross (empty curves, or all
/// buys strictly below all sells). On a cross, <see cref="Awards"/> is the
/// per-team net allocation (signed: positive = net buy, negative = net sell);
/// on no-cross, <see cref="Awards"/> is empty and <see cref="ClearingPriceTicks"/>
/// is zero.
/// </summary>
public sealed record ClearingOutcome(
    string QuarterId,
    bool DidCross,
    long ClearingPriceTicks,
    IReadOnlyList<(string TeamName, long AwardedQuantityTicks)> Awards)
{
    public static ClearingOutcome Cleared(
        string quarterId,
        long priceTicks,
        IReadOnlyList<(string, long)> awards) =>
        new(quarterId, true, priceTicks, awards);

    public static ClearingOutcome NoCross(string quarterId) =>
        new(quarterId, false, 0L, Array.Empty<(string, long)>());
}
