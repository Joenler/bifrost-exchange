namespace Bifrost.Exchange.Domain;

public sealed class MatchingEngine(OrderBook book, ISequenceGenerator sequenceGenerator)
{
    public OrderBook Book => book;

    public MatchingResult SubmitOrder(Order order)
    {
        var result = new MatchingResult();

        if (order.OrderType == OrderType.FillOrKill)
        {
            if (!CanFillCompletely(order))
            {
                order.Reject();
                result.EmitRejected(order, RejectionCode.InsufficientLiquidityForFok);
                return result;
            }
        }

        order.AssignTimePriority(sequenceGenerator.Next());
        result.EmitAccepted(order);

        MatchAggressor(order, result);

        if (order.OrderType == OrderType.Market)
        {
            if (order.OpenQuantity <= Quantity.Zero) return result;
            var cancelledQty = order.OpenQuantity;
            order.Cancel();
            result.EmitMarketOrderRemainderCancelled(order, cancelledQty);
        }
        else if (order.OrderType != OrderType.FillOrKill)
        {
            if (order.OpenQuantity <= Quantity.Zero || order.Status == OrderStatus.Filled) return result;

            if (order.OrderType == OrderType.Iceberg
                && order.DisplayedQuantity == Quantity.Zero)
            {
                var refreshPriority = sequenceGenerator.Next();
                if (order.TryRefreshIcebergSlice(refreshPriority))
                {
                    result.EmitIcebergRefresh(new IcebergRefresh(
                        order.OrderId,
                        order.DisplayedQuantity,
                        refreshPriority));
                }
            }

            book.AddOrder(order);
            result.EmitBookChange(order.Side, order.Price);
        }

        return result;
    }

    public MatchingResult CancelOrder(OrderId orderId, ClientId requestingClient)
    {
        var result = new MatchingResult();
        var order = book.GetOrder(orderId);

        if (order is null)
        {
            var phantom = Order.CreateLimit(orderId, requestingClient, book.InstrumentId,
                Side.Buy, new Price(0), Quantity.Zero, new SequenceNumber(0));
            result.EmitRejected(phantom, RejectionCode.OrderNotFound);
            return result;
        }

        if (order.ClientId != requestingClient)
        {
            var phantom = Order.CreateLimit(orderId, requestingClient, book.InstrumentId,
                Side.Buy, new Price(0), Quantity.Zero, new SequenceNumber(0));
            result.EmitRejected(phantom, RejectionCode.NotAuthorizedToCancel);
            return result;
        }

        var price = order.Price;
        var side = order.Side;

        var remainingQty = order.OpenQuantity;
        order.Cancel();
        book.RemoveOrder(order);
        result.EmitCancelled(order, remainingQty);
        result.EmitBookChange(side, price);

        return result;
    }

    public MatchingResult ReplaceOrder(
        OrderId existingOrderId,
        ClientId requestingClient,
        Price? newPrice,
        Quantity? newQuantity)
    {
        var result = new MatchingResult();
        var existingOrder = book.GetOrder(existingOrderId);

        if (existingOrder is null)
        {
            var phantom = Order.CreateLimit(existingOrderId, requestingClient, book.InstrumentId,
                Side.Buy, new Price(0), Quantity.Zero, new SequenceNumber(0));
            result.EmitRejected(phantom, RejectionCode.OrderNotFound);
            return result;
        }

        if (existingOrder.ClientId != requestingClient)
        {
            var phantom = Order.CreateLimit(existingOrderId, requestingClient, book.InstrumentId,
                Side.Buy, new Price(0), Quantity.Zero, new SequenceNumber(0));
            result.EmitRejected(phantom, RejectionCode.NotAuthorizedToReplace);
            return result;
        }

        var oldPrice = existingOrder.Price;
        var side = existingOrder.Side;
        var priceChanged = newPrice.HasValue && newPrice.Value != existingOrder.Price;
        var quantityUp = newQuantity.HasValue && newQuantity.Value > existingOrder.TotalQuantity;

        if (quantityUp)
        {
            result.EmitRejected(existingOrder, RejectionCode.QuantityIncreaseNotSupported);
            return result;
        }

        if (newQuantity.HasValue)
        {
            var filledQuantity = existingOrder.TotalQuantity - existingOrder.OpenQuantity;
            if (newQuantity.Value < filledQuantity)
            {
                result.EmitRejected(existingOrder, RejectionCode.NewQuantityBelowFilledAmount);
                return result;
            }
        }

        result.EmitBookChange(side, oldPrice);

        if (priceChanged)
        {
            book.RemoveOrder(existingOrder);

            var newPriority = sequenceGenerator.Next();
            existingOrder.UpdatePrice(newPrice!.Value, newPriority);

            if (newQuantity.HasValue)
            {
                existingOrder.UpdateQuantityDown(newQuantity.Value);
            }

            MatchAggressor(existingOrder, result);

            if (existingOrder.OpenQuantity > Quantity.Zero
                && existingOrder.Status != OrderStatus.Filled)
            {
                if (existingOrder.OrderType == OrderType.Iceberg
                    && existingOrder.DisplayedQuantity == Quantity.Zero)
                {
                    var refreshPriority = sequenceGenerator.Next();
                    if (existingOrder.TryRefreshIcebergSlice(refreshPriority))
                    {
                        result.EmitIcebergRefresh(new IcebergRefresh(
                            existingOrder.OrderId,
                            existingOrder.DisplayedQuantity,
                            refreshPriority));
                    }
                }

                book.AddOrder(existingOrder);
            }

            result.EmitBookChange(side, newPrice!.Value);
        }
        else if (newQuantity.HasValue)
        {
            existingOrder.UpdateQuantityDown(newQuantity.Value);

            var level = book.GetLevel(side, existingOrder.Price);
            level?.RecalculateVisibleQuantity();
        }

        result.EmitAccepted(existingOrder);
        return result;
    }

    private bool CanFillCompletely(Order order)
    {
        var contraSide = order.Side == Side.Buy ? Side.Sell : Side.Buy;
        var accumulated = Quantity.Zero;

        foreach (var level in book.GetLevels(contraSide))
        {
            if (order.Side == Side.Buy && level.Price > order.Price)
                break;
            if (order.Side == Side.Sell && level.Price < order.Price)
                break;

            foreach (var resting in level.Orders)
            {
                accumulated += resting.OpenQuantity;
                if (accumulated >= order.TotalQuantity)
                    return true;
            }
        }

        return accumulated >= order.TotalQuantity;
    }

    private void MatchAggressor(Order aggressor, MatchingResult result)
    {
        var contraSide = aggressor.Side == Side.Buy ? Side.Sell : Side.Buy;

        while (aggressor.OpenQuantity > Quantity.Zero)
        {
            var bestPrice = contraSide == Side.Sell ? book.BestAsk : book.BestBid;
            if (bestPrice is null)
                break;

            if (aggressor.OrderType != OrderType.Market)
            {
                if (aggressor.Side == Side.Buy && bestPrice.Value > aggressor.Price)
                    break;
                if (aggressor.Side == Side.Sell && bestPrice.Value < aggressor.Price)
                    break;
            }

            var prevOpenQty = aggressor.OpenQuantity;
            var level = book.GetLevel(contraSide, bestPrice.Value)!;
            MatchAtLevel(aggressor, level, bestPrice.Value, contraSide, result);

            if (aggressor.OpenQuantity == prevOpenQty)
                break;
        }
    }

    private void MatchAtLevel(
        Order aggressor,
        PriceLevel level,
        Price levelPrice,
        Side contraSide,
        MatchingResult result)
    {
        while (aggressor.OpenQuantity > Quantity.Zero)
        {
            var restingOrder = level.PeekFirst();
            if (restingOrder is null)
                break;

            var fillQty = Quantity.Min(aggressor.OpenQuantity, restingOrder.DisplayedQuantity);
            if (fillQty <= Quantity.Zero)
                break;

            restingOrder.Fill(fillQty);
            aggressor.Fill(fillQty);

            var tradeId = new TradeId(sequenceGenerator.Next().Value);
            var trade = new Trade(
                tradeId,
                aggressor.OrderId,
                aggressor.ClientId,
                restingOrder.OrderId,
                restingOrder.ClientId,
                book.InstrumentId,
                levelPrice,
                fillQty,
                aggressor.Side,
                aggressor.OpenQuantity,
                restingOrder.OpenQuantity);

            result.EmitTrade(trade);
            result.EmitBookChange(contraSide, levelPrice);

            if (restingOrder.Status == OrderStatus.Filled)
            {
                book.RemoveOrder(restingOrder);
            }
            else if (restingOrder.DisplayedQuantity == Quantity.Zero
                     && restingOrder.OrderType == OrderType.Iceberg)
            {
                var newPriority = sequenceGenerator.Next();
                if (!restingOrder.TryRefreshIcebergSlice(newPriority))
                {
                    book.RemoveOrder(restingOrder);
                    continue;
                }
                result.EmitIcebergRefresh(new IcebergRefresh(
                    restingOrder.OrderId,
                    restingOrder.DisplayedQuantity,
                    newPriority));

                // Remove and re-add to move to back of queue
                book.RemoveOrder(restingOrder);
                book.AddOrder(restingOrder);
            }
        }

        level.RecalculateVisibleQuantity();
    }
}
