namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange-to-trader notification of a trade execution against an order.
/// </summary>
public sealed record OrderExecutedEvent(
    long TradeId,
    long OrderId,
    string ClientId,
    InstrumentIdDto InstrumentId,
    long PriceTicks,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    string Side,
    bool IsAggressor,
    decimal Fee,
    long TimestampNs);
