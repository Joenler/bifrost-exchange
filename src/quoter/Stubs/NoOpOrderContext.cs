using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Pricing;
using Microsoft.Extensions.Logging;

namespace Bifrost.Quoter.Stubs;

/// <summary>
/// Build-green no-op implementation of <see cref="IOrderContext"/>. Logs every call at
/// debug level and otherwise does nothing. Lives at this fixed path so the future
/// QuoterCommandPublisher swap-in can unconditionally delete this file.
/// </summary>
// TODO(swap): replaced and FILE DELETED when QuoterCommandPublisher lands.
public sealed class NoOpOrderContext : IOrderContext
{
    private static readonly CorrelationId NoOpCorrelationId = new("noop");

    public NoOpOrderContext(ILogger<NoOpOrderContext> logger)
    {
        Logger = logger;
    }

    public ILogger Logger { get; }

    public CorrelationId SubmitLimitOrder(InstrumentId instrument, Side side, long priceTicks, decimal qty)
    {
        Logger.LogDebug(
            "NoOp SubmitLimitOrder: {Instrument} {Side} {Price} {Qty}",
            instrument, side, priceTicks, qty);
        return NoOpCorrelationId;
    }

    public void CancelOrder(InstrumentId instrument, OrderId orderId)
    {
        Logger.LogDebug("NoOp CancelOrder: {Instrument} {OrderId}", instrument, orderId);
    }

    public void ReplaceOrder(InstrumentId instrument, OrderId orderId, long newPriceTicks, decimal? newQty)
    {
        Logger.LogDebug(
            "NoOp ReplaceOrder: {Instrument} {OrderId} {Price} {Qty}",
            instrument, orderId, newPriceTicks, newQty);
    }

    public Order? GetOrder(OrderId orderId) => null;
}
