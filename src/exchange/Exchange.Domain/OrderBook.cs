namespace Bifrost.Exchange.Domain;

public sealed class OrderBook(InstrumentId instrumentId)
{
    private sealed class DescendingPriceComparer : IComparer<Price>
    {
        public int Compare(Price x, Price y) => y.Ticks.CompareTo(x.Ticks);
    }

    private readonly SortedDictionary<Price, PriceLevel> _bids = new(new DescendingPriceComparer());
    private readonly SortedDictionary<Price, PriceLevel> _asks = new();
    private readonly Dictionary<OrderId, Order> _orderIndex = new();

    public InstrumentId InstrumentId { get; } = instrumentId;

    public IReadOnlyDictionary<Price, PriceLevel> Bids => _bids;
    public IReadOnlyDictionary<Price, PriceLevel> Asks => _asks;

    public Price? BestBid => _bids.Count > 0 ? _bids.Keys.First() : null;
    public Price? BestAsk => _asks.Count > 0 ? _asks.Keys.First() : null;

    public Order? GetOrder(OrderId orderId)
    {
        return _orderIndex.GetValueOrDefault(orderId);
    }

    public void AddOrder(Order order)
    {
        var levels = order.Side == Side.Buy ? _bids : _asks;

        // bifrost-lint: compound-ok — SortedDictionary (not ConcurrentDictionary) accessed by single-writer MatchingEngine
        if (!levels.TryGetValue(order.Price, out var level))
        {
            level = new PriceLevel(order.Price);
            levels[order.Price] = level;
        }

        level.AddOrder(order);
        _orderIndex[order.OrderId] = order;
        order.Activate();
    }

    public void RemoveOrder(Order order)
    {
        var levels = order.Side == Side.Buy ? _bids : _asks;

        if (levels.TryGetValue(order.Price, out var level))
        {
            level.RemoveOrder(order);

            if (level.IsEmpty)
            {
                levels.Remove(order.Price);
            }
        }

        _orderIndex.Remove(order.OrderId);
    }

    public PriceLevel? GetLevel(Side side, Price price)
    {
        var levels = side == Side.Buy ? _bids : _asks;
        return levels.GetValueOrDefault(price);
    }

    public IEnumerable<PriceLevel> GetLevels(Side side)
    {
        return side == Side.Buy ? _bids.Values : _asks.Values;
    }

    public int TotalOrderCount => _orderIndex.Count;
}
