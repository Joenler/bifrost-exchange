namespace Bifrost.Contracts.Internal.Auction;

/// <summary>
/// Per-team, per-quarter-hour bid matrix submitted to the DAH auction.
/// BuySteps must be strictly descending by PriceTicks; SellSteps strictly
/// ascending. Either side may be empty (buy-only / sell-only team is valid).
/// </summary>
public sealed record BidMatrixDto(
    string TeamName,
    string QuarterId,
    BidStepDto[] BuySteps,
    BidStepDto[] SellSteps);
