namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange-to-trader notification that the unfilled remainder of a market order was cancelled.
/// </summary>
public sealed record MarketOrderRemainderCancelledEvent(
    long OrderId,
    string ClientId,
    InstrumentIdDto InstrumentId,
    decimal CancelledQuantity,
    long TimestampNs);
