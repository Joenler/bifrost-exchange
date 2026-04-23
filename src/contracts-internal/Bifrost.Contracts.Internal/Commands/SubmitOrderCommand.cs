namespace Bifrost.Contracts.Internal.Commands;

/// <summary>
/// Trader-to-exchange command to submit a new order.
/// </summary>
public sealed record SubmitOrderCommand(
    string ClientId,
    InstrumentIdDto InstrumentId,
    string Side,
    string OrderType,
    long? PriceTicks,
    decimal Quantity,
    decimal? DisplaySliceSize);
