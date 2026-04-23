namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange-to-trader confirmation that an order was cancelled.
/// </summary>
public sealed record OrderCancelledEvent(
    long OrderId,
    string ClientId,
    InstrumentIdDto InstrumentId,
    decimal RemainingQuantity,
    long TimestampNs);
