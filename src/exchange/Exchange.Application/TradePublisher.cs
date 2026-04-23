using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

public sealed class TradePublisher(
    IEventPublisher publisher,
    PublicSequenceTracker sequenceTracker,
    ExchangeRulesConfig rules)
{
    public async ValueTask PublishPrivateTradeEvents(
        IReadOnlyList<TradeFilled> trades, long timestampNs, string? correlationId)
    {
        foreach (var t in trades)
        {
            var instrumentIdDto = InstrumentIdMapping.ToDto(t.InstrumentId);
            var restingSide = t.AggressorSide == Side.Buy ? Side.Sell : Side.Buy;
            var takerFee = t.Quantity.Value * rules.TakerFeeRate;
            var makerFee = t.Quantity.Value * rules.MakerFeeRate;

            var aggressorExec = new OrderExecutedEvent(
                t.TradeId.Value, t.AggressorOrderId.Value,
                t.AggressorClientId.Value, instrumentIdDto,
                t.Price.Ticks, t.Quantity.Value,
                t.AggressorRemainingQuantity.Value,
                t.AggressorSide.ToString(), true, takerFee, timestampNs);
            await publisher.PublishPrivate(t.AggressorClientId.Value, aggressorExec, correlationId);

            var restingExec = new OrderExecutedEvent(
                t.TradeId.Value, t.RestingOrderId.Value,
                t.RestingClientId.Value, instrumentIdDto,
                t.Price.Ticks, t.Quantity.Value,
                t.RestingRemainingQuantity.Value,
                restingSide.ToString(), false, makerFee, timestampNs);
            await publisher.PublishPrivate(t.RestingClientId.Value, restingExec);
        }
    }

    public async ValueTask PublishPublicTradeEvents(
        IReadOnlyList<TradeFilled> trades, InstrumentId instrumentId, long timestampNs)
    {
        foreach (var t in trades)
        {
            var instrumentIdDto = InstrumentIdMapping.ToDto(t.InstrumentId);
            var seq = sequenceTracker.Next(instrumentId);
            var publicTrade = new PublicTradeEvent(
                t.TradeId.Value, instrumentIdDto,
                t.Price.Ticks, t.Quantity.Value,
                t.AggressorSide.ToString(), rules.TickSize,
                seq, timestampNs);
            await publisher.PublishPublicTrade(instrumentId.ToRoutingKey(), publicTrade, seq);
        }
    }
}
