namespace Bifrost.Exchange.Domain;

public sealed class MatchingResult
{
    private readonly List<MatchingEvent> _events = [];

    public IReadOnlyList<MatchingEvent> Events => _events;

    public bool WasAccepted => _events.OfType<OrderAccepted>().Any();
    public bool WasRejected => _events.OfType<OrderRejected>().Any();
    public bool HasTrades => _events.OfType<TradeFilled>().Any();

    public void EmitAccepted(Order order)
    {
        _events.Add(new OrderAccepted(
            order.OrderId, order.ClientId, order.InstrumentId,
            order.Side, order.OrderType, order.Price, order.TotalQuantity,
            order.OrderType == OrderType.Iceberg ? order.DisplaySliceSize : null,
            order.TimePriority));
    }

    public void EmitRejected(Order order, RejectionCode code, string? detail = null)
    {
        _events.Add(new OrderRejected(
            order.OrderId, order.ClientId, order.InstrumentId,
            code, detail));
    }

    public void EmitCancelled(Order order, Quantity remainingQuantity)
    {
        _events.Add(new OrderCancelled(
            order.OrderId, order.ClientId, order.InstrumentId,
            order.Side, order.Price, remainingQuantity));
    }

    public void EmitMarketOrderRemainderCancelled(Order order, Quantity cancelledQuantity)
    {
        _events.Add(new MarketOrderRemainderCancelled(
            order.OrderId, order.ClientId, order.InstrumentId,
            cancelledQuantity));
    }

    public void EmitTrade(Trade trade)
    {
        _events.Add(new TradeFilled(
            trade.TradeId, trade.AggressorOrderId, trade.AggressorClientId,
            trade.RestingOrderId, trade.RestingClientId, trade.InstrumentId,
            trade.Price, trade.Quantity, trade.AggressorSide,
            trade.AggressorRemainingQuantity, trade.RestingRemainingQuantity));
    }

    public void EmitIcebergRefresh(IcebergRefresh refresh)
    {
        _events.Add(new IcebergSliceRefreshed(
            refresh.OrderId, refresh.NewDisplayedQuantity, refresh.NewPriority));
    }

    public void EmitBookChange(Side side, Price price)
    {
        _events.Add(new BookLevelChanged(side, price));
    }
}
