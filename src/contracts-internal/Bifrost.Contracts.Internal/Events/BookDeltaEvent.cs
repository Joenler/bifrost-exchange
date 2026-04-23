namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Incremental order book update for a single price level.
/// </summary>
public sealed record BookDeltaEvent(
    InstrumentIdDto InstrumentId,
    long Sequence,
    BookLevelDto[] ChangedBids,
    BookLevelDto[] ChangedAsks,
    long TimestampNs);
