namespace Bifrost.Exchange.Domain;

public sealed class Order
{
    public OrderId OrderId { get; }
    public ClientId ClientId { get; }
    public InstrumentId InstrumentId { get; }
    public Side Side { get; }
    public OrderType OrderType { get; }
    public Price Price { get; private set; }
    public Quantity TotalQuantity { get; private set; }
    public Quantity OpenQuantity { get; private set; }
    public Quantity DisplayedQuantity { get; private set; }
    public Quantity HiddenQuantity { get; private set; }
    public Quantity DisplaySliceSize { get; }
    public SequenceNumber TimePriority { get; private set; }
    public OrderStatus Status { get; private set; }

    // Node reference for O(1) removal from price level queue
    internal LinkedListNode<Order>? BookNode { get; set; }

    private Order(
        OrderId orderId,
        ClientId clientId,
        InstrumentId instrumentId,
        Side side,
        OrderType orderType,
        Price price,
        Quantity totalQuantity,
        Quantity displaySliceSize,
        SequenceNumber timePriority)
    {
        OrderId = orderId;
        ClientId = clientId;
        InstrumentId = instrumentId;
        Side = side;
        OrderType = orderType;
        Price = price;
        TotalQuantity = totalQuantity;
        DisplaySliceSize = displaySliceSize;
        TimePriority = timePriority;
        Status = OrderStatus.New;

        if (orderType == OrderType.Iceberg)
        {
            var displayed = Quantity.Min(displaySliceSize, totalQuantity);
            DisplayedQuantity = displayed;
            HiddenQuantity = totalQuantity - displayed;
            OpenQuantity = totalQuantity;
        }
        else
        {
            DisplayedQuantity = totalQuantity;
            HiddenQuantity = Quantity.Zero;
            OpenQuantity = totalQuantity;
        }
    }

    public static Order CreateLimit(
        OrderId orderId, ClientId clientId, InstrumentId instrumentId,
        Side side, Price price, Quantity quantity, SequenceNumber timePriority)
    {
        return new Order(orderId, clientId, instrumentId, side, OrderType.Limit,
            price, quantity, Quantity.Zero, timePriority);
    }

    public static Order CreateMarket(
        OrderId orderId, ClientId clientId, InstrumentId instrumentId,
        Side side, Quantity quantity, SequenceNumber timePriority)
    {
        // Market orders have no price; use 0 as placeholder (never rests)
        return new Order(orderId, clientId, instrumentId, side, OrderType.Market,
            new Price(0), quantity, Quantity.Zero, timePriority);
    }

    public static Order CreateFillOrKill(
        OrderId orderId, ClientId clientId, InstrumentId instrumentId,
        Side side, Price price, Quantity quantity, SequenceNumber timePriority)
    {
        return new Order(orderId, clientId, instrumentId, side, OrderType.FillOrKill,
            price, quantity, Quantity.Zero, timePriority);
    }

    public static Order CreateIceberg(
        OrderId orderId, ClientId clientId, InstrumentId instrumentId,
        Side side, Price price, Quantity totalQuantity, Quantity displaySliceSize,
        SequenceNumber timePriority)
    {
        return new Order(orderId, clientId, instrumentId, side, OrderType.Iceberg,
            price, totalQuantity, displaySliceSize, timePriority);
    }

    private void AssertNotTerminal()
    {
        if (Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected)
            throw new InvalidOperationException(
                $"Cannot mutate order {OrderId} in terminal state {Status}");
    }

    public Quantity Fill(Quantity fillQuantity)
    {
        AssertNotTerminal();

        if (fillQuantity > OpenQuantity)
            throw new InvalidOperationException(
                $"Fill quantity {fillQuantity} exceeds open quantity {OpenQuantity}");

        OpenQuantity -= fillQuantity;

        if (fillQuantity <= DisplayedQuantity)
        {
            DisplayedQuantity -= fillQuantity;
        }
        else
        {
            var fromHidden = fillQuantity - DisplayedQuantity;
            HiddenQuantity -= fromHidden;
            DisplayedQuantity = Quantity.Zero;
        }

        Status = OpenQuantity == Quantity.Zero ? OrderStatus.Filled : OrderStatus.PartiallyFilled;

        return fillQuantity;
    }

    public bool TryRefreshIcebergSlice(SequenceNumber newPriority)
    {
        AssertNotTerminal();

        if (OrderType != OrderType.Iceberg)
            return false;

        if (DisplayedQuantity > Quantity.Zero)
            return false;

        if (HiddenQuantity <= Quantity.Zero)
            return false;

        var newSlice = Quantity.Min(DisplaySliceSize, HiddenQuantity);
        HiddenQuantity -= newSlice;
        DisplayedQuantity = newSlice;
        TimePriority = newPriority;
        Status = OrderStatus.Active;

        return true;
    }

    public void Cancel()
    {
        AssertNotTerminal();

        Status = OrderStatus.Cancelled;
        OpenQuantity = Quantity.Zero;
        DisplayedQuantity = Quantity.Zero;
        HiddenQuantity = Quantity.Zero;
    }

    public void Reject()
    {
        AssertNotTerminal();

        Status = OrderStatus.Rejected;
        OpenQuantity = Quantity.Zero;
        DisplayedQuantity = Quantity.Zero;
        HiddenQuantity = Quantity.Zero;
    }

    public void AssignTimePriority(SequenceNumber priority)
    {
        TimePriority = priority;
    }

    public void Activate()
    {
        AssertNotTerminal();

        if (Status == OrderStatus.New)
            Status = OrderStatus.Active;
    }

    public void UpdatePrice(Price newPrice, SequenceNumber newPriority)
    {
        AssertNotTerminal();

        Price = newPrice;
        TimePriority = newPriority;
    }

    public void UpdateQuantityDown(Quantity newTotalQuantity)
    {
        AssertNotTerminal();

        var reduction = TotalQuantity - newTotalQuantity;
        TotalQuantity = newTotalQuantity;

        if (OrderType == OrderType.Iceberg)
        {
            if (HiddenQuantity >= reduction)
            {
                HiddenQuantity -= reduction;
            }
            else
            {
                var remainingReduction = reduction - HiddenQuantity;
                HiddenQuantity = Quantity.Zero;
                DisplayedQuantity -= remainingReduction;
            }

            OpenQuantity = DisplayedQuantity + HiddenQuantity;
        }
        else
        {
            OpenQuantity -= reduction;
            DisplayedQuantity = OpenQuantity;
        }
    }
}
