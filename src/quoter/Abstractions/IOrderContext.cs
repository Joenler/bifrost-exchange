using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Pricing;
using Microsoft.Extensions.Logging;

namespace Bifrost.Quoter.Abstractions;

/// <summary>
/// Narrow order-context surface the quoter pricing layer (PyramidQuoteTracker, QuoteSide)
/// depends on. Exposes only the operations needed for limit-order submit / cancel / replace
/// plus a query hook used by the book-consistency guard. Implemented by the quoter command
/// publisher that bridges these calls onto the internal RabbitMQ command fabric.
/// </summary>
public interface IOrderContext
{
    /// <summary>
    /// Submits a new limit order. Returns the deterministic correlation identifier the
    /// publisher will stamp onto the outbound command.
    /// </summary>
    CorrelationId SubmitLimitOrder(InstrumentId instrument, Side side, long priceTicks, decimal qty);

    /// <summary>
    /// Issues a cancel against a known OrderId previously accepted by the matching engine.
    /// </summary>
    void CancelOrder(InstrumentId instrument, OrderId orderId);

    /// <summary>
    /// Issues a replace against a known OrderId. Callers must have applied the
    /// book-consistency guard (skip when the tracked level is absent from the current
    /// BookView) before invoking this method.
    /// </summary>
    void ReplaceOrder(InstrumentId instrument, OrderId orderId, long newPriceTicks, decimal? newQty);

    /// <summary>
    /// Returns the currently tracked Order for the given OrderId, or null if unknown.
    /// Used by the book-consistency guard to compare tracked price against BookView.
    /// </summary>
    Order? GetOrder(OrderId orderId);

    /// <summary>
    /// Logger surface the quoter pricing layer writes its diagnostic / warning lines to.
    /// </summary>
    ILogger Logger { get; }
}
