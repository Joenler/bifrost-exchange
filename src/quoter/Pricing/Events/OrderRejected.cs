using Bifrost.Exchange.Domain;

namespace Bifrost.Quoter.Pricing.Events;

/// <summary>
/// Internal event indicating an order was rejected by the exchange.
/// </summary>
public sealed record OrderRejected(
    OrderId OrderId,
    string Reason,
    CorrelationId? CorrelationId,
    long ExchangeTimestampNs = 0);
