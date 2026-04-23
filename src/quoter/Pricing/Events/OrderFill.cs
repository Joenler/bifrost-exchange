using Bifrost.Exchange.Domain;

namespace Bifrost.Quoter.Pricing.Events;

/// <summary>
/// Internal event representing a trade execution against an order.
/// </summary>
public sealed record OrderFill(
    TradeId TradeId,
    OrderId OrderId,
    InstrumentId Instrument,
    long PriceTicks,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    Side Side,
    bool IsAggressor,
    decimal Fee,
    CorrelationId? CorrelationId,
    long ExchangeTimestampNs = 0);
