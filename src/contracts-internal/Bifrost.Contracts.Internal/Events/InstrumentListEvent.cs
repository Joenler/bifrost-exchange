namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Exchange broadcast of the full instrument catalog.
/// </summary>
public sealed record InstrumentListEvent(InstrumentIdDto[] Instruments, long TimestampNs);
