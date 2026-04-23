namespace Bifrost.Exchange.Domain;

public sealed record Trade(
    TradeId TradeId,
    OrderId AggressorOrderId,
    ClientId AggressorClientId,
    OrderId RestingOrderId,
    ClientId RestingClientId,
    InstrumentId InstrumentId,
    Price Price,
    Quantity Quantity,
    Side AggressorSide,
    Quantity AggressorRemainingQuantity,
    Quantity RestingRemainingQuantity);
