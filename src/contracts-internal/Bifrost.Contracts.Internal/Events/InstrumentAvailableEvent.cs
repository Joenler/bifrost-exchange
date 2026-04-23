namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange broadcast that a new instrument is available for trading.
/// </summary>
public sealed record InstrumentAvailableEvent(InstrumentIdDto InstrumentId, long TimestampNs);
