namespace Bifrost.Exchange.Domain;

public abstract record MatchingEvent;

public sealed record OrderAccepted(
    OrderId OrderId,
    ClientId ClientId,
    InstrumentId InstrumentId,
    Side Side,
    OrderType OrderType,
    Price Price,
    Quantity Quantity,
    Quantity? DisplaySliceSize,
    SequenceNumber TimePriority) : MatchingEvent;

public sealed record OrderRejected(
    OrderId OrderId,
    ClientId ClientId,
    InstrumentId InstrumentId,
    RejectionCode Code,
    string? Detail = null) : MatchingEvent;

public sealed record TradeFilled(
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
    Quantity RestingRemainingQuantity) : MatchingEvent;

public sealed record OrderCancelled(
    OrderId OrderId,
    ClientId ClientId,
    InstrumentId InstrumentId,
    Side Side,
    Price Price,
    Quantity RemainingQuantity) : MatchingEvent;

public sealed record MarketOrderRemainderCancelled(
    OrderId OrderId,
    ClientId ClientId,
    InstrumentId InstrumentId,
    Quantity CancelledQuantity) : MatchingEvent;

public sealed record IcebergSliceRefreshed(
    OrderId OrderId,
    Quantity NewDisplayedQuantity,
    SequenceNumber NewPriority) : MatchingEvent;

public sealed record BookLevelChanged(
    Side Side,
    Price Price) : MatchingEvent;
