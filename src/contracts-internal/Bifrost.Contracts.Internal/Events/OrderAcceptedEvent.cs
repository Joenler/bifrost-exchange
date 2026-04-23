namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange-to-trader confirmation that an order was accepted and assigned an order ID.
/// </summary>
public sealed record OrderAcceptedEvent(
    long OrderId,
    string ClientId,
    InstrumentIdDto InstrumentId,
    string Side,
    string OrderType,
    long? PriceTicks,
    decimal Quantity,
    decimal? DisplaySliceSize,
    long TimestampNs);
