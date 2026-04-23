namespace Bifrost.Contracts.Internal.Events;

public sealed record PublicTradeEvent(
    long TradeId,
    InstrumentIdDto InstrumentId,
    long PriceTicks,
    decimal Quantity,
    string AggressorSide,
    long TickSize,
    long Sequence,
    long TimestampNs);
