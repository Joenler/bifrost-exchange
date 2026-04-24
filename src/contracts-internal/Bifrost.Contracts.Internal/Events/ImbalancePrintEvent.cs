namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Public realized imbalance price per quarter, broadcast at RoundState=Gate
/// (4 per round; zero during other states). ATotalTicks decomposes into the
/// separately visible APhysicalTicks so consumers can isolate the physical
/// component from the team-contributed aggregate.
/// </summary>
public sealed record ImbalancePrintEvent(
    int RoundNumber,
    InstrumentIdDto InstrumentId,
    int QuarterIndex,
    long PImbTicks,
    long ATotalTicks,
    long APhysicalTicks,
    string Regime,
    long TimestampNs);
