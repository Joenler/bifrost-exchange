namespace Bifrost.Contracts.Internal.Commands;

/// <summary>
/// Trader-to-exchange command to modify an existing order's price and quantity.
/// </summary>
public sealed record ReplaceOrderCommand(
    string ClientId,
    long OrderId,
    long? NewPriceTicks,
    decimal? NewQuantity,
    InstrumentIdDto InstrumentId);
