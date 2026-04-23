using Bifrost.Exchange.Domain;

namespace Bifrost.Quoter.Pricing.Events;

/// <summary>
/// Internal event indicating an order was cancelled.
/// </summary>
public sealed record OrderCancelled(
    OrderId OrderId,
    InstrumentId Instrument,
    decimal RemainingQuantity,
    CorrelationId? CorrelationId,
    long ExchangeTimestampNs = 0);
