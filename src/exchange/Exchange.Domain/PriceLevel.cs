namespace Bifrost.Exchange.Domain;

public sealed class PriceLevel(Price price)
{
    private readonly LinkedList<Order> _orders = [];

    public Price Price { get; } = price;
    public int OrderCount => _orders.Count;
    public bool IsEmpty => _orders.Count == 0;
    public Quantity TotalVisibleQuantity { get; private set; } = Quantity.Zero;

    public void AddOrder(Order order)
    {
        var node = _orders.AddLast(order);
        order.BookNode = node;
        TotalVisibleQuantity += order.DisplayedQuantity;
    }

    public void RemoveOrder(Order order)
    {
        if (order.BookNode is null)
            return;

        TotalVisibleQuantity -= order.DisplayedQuantity;
        _orders.Remove(order.BookNode);
        order.BookNode = null;
    }

    public void RecalculateVisibleQuantity()
    {
        var total = Quantity.Zero;
        foreach (var order in _orders)
        {
            total += order.DisplayedQuantity;
        }
        TotalVisibleQuantity = total;
    }

    public Order? PeekFirst() => _orders.First?.Value;

    public IEnumerable<Order> Orders => _orders;
}
