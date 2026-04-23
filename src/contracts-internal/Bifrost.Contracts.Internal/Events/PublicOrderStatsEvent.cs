namespace Bifrost.Contracts.Internal.Events;

public sealed record PublicOrderStatsEvent(
    InstrumentIdDto InstrumentId,
    int Submits,
    int Cancels,
    int Fills,
    int MarketOrders,
    int QuoteUpdates,
    long TimestampNs);
