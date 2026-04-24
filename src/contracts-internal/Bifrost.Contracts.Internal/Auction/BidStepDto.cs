namespace Bifrost.Contracts.Internal.Auction;

/// <summary>
/// Single marginal step in a DAH bid matrix: a (price, quantity) pair indicating
/// the team is willing to buy (on buy_steps) quantity_ticks at or BELOW price_ticks,
/// or sell (on sell_steps) quantity_ticks at or ABOVE price_ticks. Prices may be
/// negative (Nordic / Central European DAH convention).
/// </summary>
public sealed record BidStepDto(long PriceTicks, long QuantityTicks);
