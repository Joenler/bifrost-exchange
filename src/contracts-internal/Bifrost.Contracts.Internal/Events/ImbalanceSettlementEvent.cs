namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Private per-team per-quarter imbalance settlement row, emitted at RoundState=Settled.
/// ImbalancePnlTicks is the exact integer product PositionTicks × PImbTicks.
/// </summary>
public sealed record ImbalanceSettlementEvent(
    int RoundNumber,
    string ClientId,
    InstrumentIdDto InstrumentId,
    int QuarterIndex,
    long PositionTicks,
    long PImbTicks,
    long ImbalancePnlTicks,
    long TimestampNs);
