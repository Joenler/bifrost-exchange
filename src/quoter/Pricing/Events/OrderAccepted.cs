using Bifrost.Exchange.Domain;

namespace Bifrost.Quoter.Pricing.Events;

/// <summary>
/// Internal event indicating an order was accepted by the exchange.
/// </summary>
public sealed record OrderAccepted(
    OrderId OrderId,
    InstrumentId Instrument,
    Side Side,
    OrderType OrderType,
    long? PriceTicks,
    decimal Quantity,
    decimal? DisplaySliceSize,
    CorrelationId? CorrelationId,
    long ExchangeTimestampNs = 0);
