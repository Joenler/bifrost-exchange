namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Full order book state for an instrument, sent in response to a snapshot request or on initial subscription.
/// </summary>
public sealed record BookSnapshotResponse(
    InstrumentIdDto InstrumentId,
    long Sequence,
    BookLevelDto[] Bids,
    BookLevelDto[] Asks,
    long TimestampNs);
