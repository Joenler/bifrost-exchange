using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Domain;
using Bifrost.Time;

namespace Bifrost.Exchange.Application;

public sealed class ExchangeService(
    OrderValidator validator,
    BookPublisher bookPublisher,
    TradePublisher tradePublisher,
    InstrumentRegistry registry,
    IEventPublisher publisher,
    PublicSequenceTracker sequenceTracker,
    ISequenceGenerator orderIdGenerator,
    IClock clock,
    ExchangeRulesConfig rules)
{
    public async Task HandleSubmitOrder(SubmitOrderCommand cmd, string? replyTo, string? correlationId)
    {
        var ts = TimestampHelper.ToUnixNanoseconds(clock.GetUtcNow());

        var validation = validator.ValidateSubmit(cmd);
        if (!validation.IsValid)
        {
            await PublishPrivateOrReply(cmd.ClientId,
                new OrderRejectedEvent(0, cmd.ClientId, validation.RejectionReason!, ts),
                replyTo, correlationId);
            return;
        }

        var instrumentId = validation.InstrumentId;
        var engine = validation.Engine!;
        var side = validation.Side;
        var orderType = validation.OrderType;

        var orderId = new OrderId(orderIdGenerator.Next().Value);
        var clientId = new ClientId(cmd.ClientId);
        var quantity = new Quantity(cmd.Quantity);

        Order order;
        switch (orderType)
        {
            case OrderType.Limit:
                if (!cmd.PriceTicks.HasValue)
                {
                    await PublishPrivateOrReply(cmd.ClientId,
                        new OrderRejectedEvent(orderId.Value, cmd.ClientId, "Limit order requires price", ts),
                        replyTo, correlationId);
                    return;
                }
                order = Order.CreateLimit(orderId, clientId, instrumentId, side,
                    new Price(cmd.PriceTicks.Value), quantity, default);
                break;
            case OrderType.Market:
                order = Order.CreateMarket(orderId, clientId, instrumentId, side, quantity, default);
                break;
            case OrderType.Iceberg:
                if (!cmd.PriceTicks.HasValue || !cmd.DisplaySliceSize.HasValue)
                {
                    await PublishPrivateOrReply(cmd.ClientId,
                        new OrderRejectedEvent(orderId.Value, cmd.ClientId,
                            "Iceberg order requires price and display slice size", ts),
                        replyTo, correlationId);
                    return;
                }
                order = Order.CreateIceberg(orderId, clientId, instrumentId, side,
                    new Price(cmd.PriceTicks.Value), quantity,
                    new Quantity(cmd.DisplaySliceSize.Value), default);
                break;
            case OrderType.FillOrKill:
                if (!cmd.PriceTicks.HasValue)
                {
                    await PublishPrivateOrReply(cmd.ClientId,
                        new OrderRejectedEvent(orderId.Value, cmd.ClientId,
                            "Fill-or-Kill order requires price", ts),
                        replyTo, correlationId);
                    return;
                }
                order = Order.CreateFillOrKill(orderId, clientId, instrumentId, side,
                    new Price(cmd.PriceTicks.Value), quantity, default);
                break;
            default:
                return;
        }

        var result = engine.SubmitOrder(order);

        var trades = new List<TradeFilled>();
        var affectedBidPrices = new HashSet<Price>();
        var affectedAskPrices = new HashSet<Price>();

        foreach (var evt in result.Events)
        {
            switch (evt)
            {
                case OrderRejected r:
                    var reason = r.Detail ?? r.Code.ToDisplayString();
                    await PublishPrivateOrReply(cmd.ClientId,
                        new OrderRejectedEvent(orderId.Value, cmd.ClientId, reason, ts),
                        replyTo, correlationId);
                    return;

                case OrderAccepted a:
                    var priceTicks = a.OrderType == OrderType.Market ? null : (long?)a.Price.Ticks;
                    var accepted = new OrderAcceptedEvent(
                        a.OrderId.Value, a.ClientId.Value,
                        InstrumentIdMapping.ToDto(a.InstrumentId),
                        a.Side.ToString(), a.OrderType.ToString(),
                        priceTicks, a.Quantity.Value,
                        a.DisplaySliceSize?.Value, ts);
                    await PublishPrivateOrReply(cmd.ClientId, accepted, replyTo, correlationId);
                    break;

                case TradeFilled t:
                    trades.Add(t);
                    break;

                case MarketOrderRemainderCancelled m:
                    var cancelledEvent = new MarketOrderRemainderCancelledEvent(
                        m.OrderId.Value, m.ClientId.Value,
                        InstrumentIdMapping.ToDto(m.InstrumentId),
                        m.CancelledQuantity.Value, ts);
                    await PublishPrivateOrReply(cmd.ClientId, cancelledEvent, replyTo, correlationId);
                    break;

                case BookLevelChanged blc:
                    if (blc.Side == Side.Buy)
                        affectedBidPrices.Add(blc.Price);
                    else
                        affectedAskPrices.Add(blc.Price);
                    break;
            }
        }

        await tradePublisher.PublishPrivateTradeEvents(trades, ts, correlationId);
        await tradePublisher.PublishPublicTradeEvents(trades, instrumentId, ts);
        await bookPublisher.PublishBookUpdate(engine, affectedBidPrices, affectedAskPrices, instrumentId, ts);
    }

    public async Task HandleCancelOrder(CancelOrderCommand cmd, string? replyTo, string? correlationId)
    {
        var ts = TimestampHelper.ToUnixNanoseconds(clock.GetUtcNow());

        var validation = validator.ValidateCancel(cmd);
        if (!validation.IsValid)
        {
            await PublishPrivateOrReply(cmd.ClientId,
                new OrderRejectedEvent(cmd.OrderId, cmd.ClientId, validation.RejectionReason!, ts),
                replyTo, correlationId);
            return;
        }

        var instrumentId = validation.InstrumentId;
        var engine = validation.Engine!;

        var result = engine.CancelOrder(new OrderId(cmd.OrderId), new ClientId(cmd.ClientId));

        var affectedBidPrices = new HashSet<Price>();
        var affectedAskPrices = new HashSet<Price>();

        foreach (var evt in result.Events)
        {
            switch (evt)
            {
                case OrderRejected r:
                    var reason = r.Detail ?? r.Code.ToDisplayString();
                    await PublishPrivateOrReply(cmd.ClientId,
                        new OrderRejectedEvent(cmd.OrderId, cmd.ClientId, reason, ts),
                        replyTo, correlationId);
                    return;

                case OrderCancelled c:
                    var cancelled = new OrderCancelledEvent(
                        c.OrderId.Value, c.ClientId.Value,
                        InstrumentIdMapping.ToDto(c.InstrumentId),
                        c.RemainingQuantity.Value, ts);
                    await PublishPrivateOrReply(cmd.ClientId, cancelled, replyTo, correlationId);
                    break;

                case BookLevelChanged blc:
                    if (blc.Side == Side.Buy)
                        affectedBidPrices.Add(blc.Price);
                    else
                        affectedAskPrices.Add(blc.Price);
                    break;
            }
        }

        await bookPublisher.PublishBookUpdate(engine, affectedBidPrices, affectedAskPrices, instrumentId, ts);
    }

    public async Task HandleReplaceOrder(ReplaceOrderCommand cmd, string? replyTo, string? correlationId)
    {
        var ts = TimestampHelper.ToUnixNanoseconds(clock.GetUtcNow());

        var validation = validator.ValidateReplace(cmd);
        if (!validation.IsValid)
        {
            await PublishPrivateOrReply(cmd.ClientId,
                new OrderRejectedEvent(cmd.OrderId, cmd.ClientId, validation.RejectionReason!, ts),
                replyTo, correlationId);
            return;
        }

        var instrumentId = validation.InstrumentId;
        var engine = validation.Engine!;

        var newPrice = cmd.NewPriceTicks.HasValue ? new Price(cmd.NewPriceTicks.Value) : (Price?)null;
        var newQuantity = cmd.NewQuantity.HasValue ? new Quantity(cmd.NewQuantity.Value) : (Quantity?)null;

        var result = engine.ReplaceOrder(new OrderId(cmd.OrderId), new ClientId(cmd.ClientId), newPrice, newQuantity);

        var trades = new List<TradeFilled>();
        var affectedBidPrices = new HashSet<Price>();
        var affectedAskPrices = new HashSet<Price>();

        foreach (var evt in result.Events)
        {
            switch (evt)
            {
                case OrderRejected r:
                    var reason = r.Detail ?? r.Code.ToDisplayString();
                    await PublishPrivateOrReply(cmd.ClientId,
                        new OrderRejectedEvent(cmd.OrderId, cmd.ClientId, reason, ts),
                        replyTo, correlationId);
                    return;

                case OrderAccepted a:
                    var priceTicks = a.OrderType == OrderType.Market ? null : (long?)a.Price.Ticks;
                    var accepted = new OrderAcceptedEvent(
                        a.OrderId.Value, a.ClientId.Value,
                        InstrumentIdMapping.ToDto(a.InstrumentId),
                        a.Side.ToString(), a.OrderType.ToString(),
                        priceTicks, a.Quantity.Value,
                        a.DisplaySliceSize?.Value, ts);
                    await PublishPrivateOrReply(cmd.ClientId, accepted, replyTo, correlationId);
                    break;

                case TradeFilled t:
                    trades.Add(t);
                    break;

                case BookLevelChanged blc:
                    if (blc.Side == Side.Buy)
                        affectedBidPrices.Add(blc.Price);
                    else
                        affectedAskPrices.Add(blc.Price);
                    break;
            }
        }

        await tradePublisher.PublishPrivateTradeEvents(trades, ts, correlationId);
        await tradePublisher.PublishPublicTradeEvents(trades, instrumentId, ts);
        await bookPublisher.PublishBookUpdate(engine, affectedBidPrices, affectedAskPrices, instrumentId, ts);
    }

    public async Task HandleGetBookSnapshot(GetBookSnapshotRequest request, string? replyTo, string? correlationId)
    {
        var ts = TimestampHelper.ToUnixNanoseconds(clock.GetUtcNow());
        var instrumentId = InstrumentIdMapping.ToDomain(request.InstrumentId);

        var engine = registry.TryGet(instrumentId);
        if (engine is null)
        {
            var rejection = new OrderRejectedEvent(0, request.ClientId, "Unknown instrument", ts);
            await PublishPrivateOrReply(request.ClientId, rejection, replyTo, correlationId);
            return;
        }

        var seq = sequenceTracker.Current(instrumentId);
        var snapshot = BookSnapshotBuilder.Build(engine.Book, seq, ts);

        if (replyTo is not null && correlationId is not null)
            await publisher.PublishReply(replyTo, correlationId, snapshot);
        else
            await publisher.PublishPrivate(request.ClientId, snapshot);
    }

    public async Task HandleSubscribe(SubscribeCommand cmd, string? replyTo, string? correlationId)
    {
        var ts = TimestampHelper.ToUnixNanoseconds(clock.GetUtcNow());

        var instrumentDtos = registry.Instruments
            .Select(InstrumentIdMapping.ToDto)
            .ToArray();

        var instrumentList = new InstrumentListEvent(instrumentDtos, ts);
        await publisher.PublishPrivate(cmd.ClientId, instrumentList);

        var metadata = new ExchangeMetadataEvent(
            rules.TickSize, rules.MinQuantity, rules.QuantityStep,
            rules.MakerFeeRate, rules.TakerFeeRate, rules.PriceScale, ts);
        await publisher.PublishPrivate(cmd.ClientId, metadata);

        foreach (var engine in registry.GetAllEngines())
        {
            if (engine.Book.TotalOrderCount > 0)
            {
                var instrumentId = engine.Book.InstrumentId;
                var seq = sequenceTracker.Current(instrumentId);
                var snapshot = BookSnapshotBuilder.Build(engine.Book, seq, ts);
                await publisher.PublishPrivate(cmd.ClientId, snapshot);
            }
        }
    }

    private async Task PublishPrivateOrReply(string clientId, object @event,
        string? replyTo, string? correlationId)
    {
        if (replyTo is not null && correlationId is not null)
            await publisher.PublishReply(replyTo, correlationId, @event);
        else
            await publisher.PublishPrivate(clientId, @event);
    }
}
