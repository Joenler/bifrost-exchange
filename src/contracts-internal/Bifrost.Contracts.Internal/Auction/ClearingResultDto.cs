namespace Bifrost.Contracts.Internal.Auction;

/// <summary>
/// Result of uniform-price pay-as-cleared clearing for a single QH. The
/// service emits one public-summary row (TeamName = null, AwardedQuantityTicks = 0)
/// plus N per-team rows (TeamName = "alpha" etc., AwardedQuantityTicks non-zero:
/// positive = net buy, negative = net sell). On the wire, the summary row has
/// proto team_name = "" — the translation layer maps null &lt;-&gt; "".
/// </summary>
public sealed record ClearingResultDto(
    string QuarterId,
    long ClearingPriceTicks,
    long AwardedQuantityTicks,
    string? TeamName);
