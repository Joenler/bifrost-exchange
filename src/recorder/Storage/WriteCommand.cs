namespace Bifrost.Recorder.Storage;

/// <summary>
/// Discriminated union of write intents drained by <see cref="WriteLoop"/>
/// into the session database. Subtypes map 1:1 to the BIFROST-native split
/// tables in <c>Migrations/001_initial.sql</c>.
/// </summary>
/// <remarks>
/// Split-shape rationale (deviation from Arena's 3-subtype shape): BIFROST
/// records orders / fills / rejects in three separate tables instead of one
/// wide <c>order_events</c> table with a discriminator column. This keeps
/// per-team scoring queries simple (SELECT ... FROM fills WHERE taker_client_id=@id)
/// and aligns the recorder surface with the public-data / private-data split
/// the gateway enforces at the team boundary.
/// </remarks>
public abstract record WriteCommand(long ReceivedAtNs);

public sealed record OrderWrite(
    long TsNs,
    string ClientId,
    string InstrumentId,
    long OrderId,
    string Action,
    string? Side,
    long? PriceTicks,
    decimal? Quantity,
    string? OrderType,
    string? CorrelationId,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);

public sealed record FillWrite(
    long TsNs,
    string InstrumentId,
    long TradeId,
    long PriceTicks,
    decimal Quantity,
    string AggressorSide,
    string MakerClientId,
    string TakerClientId,
    long MakerOrderId,
    long TakerOrderId,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);

public sealed record RejectWrite(
    long TsNs,
    string ClientId,
    string? InstrumentId,
    string RejectionCode,
    string? ReasonDetail,
    string? CorrelationId,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);

public sealed record BookUpdateWrite(
    long TsNs,
    string InstrumentId,
    string Side,
    int Level,
    long PriceTicks,
    decimal Quantity,
    int Count,
    long Sequence,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);

public sealed record TradeWrite(
    long TsNs,
    string InstrumentId,
    long TradeId,
    long PriceTicks,
    decimal Quantity,
    string AggressorSide,
    long Sequence,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);

public sealed record EventWrite(
    long TsNs,
    string Kind,
    string Severity,
    string PayloadJson,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);

public sealed record ImbalanceSettlementWrite(
    long TsNs,
    int RoundNumber,
    string ClientId,
    string InstrumentId,
    int QuarterIndex,
    long PositionTicks,
    long PImbTicks,
    long ImbalancePnlTicks,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);

/// <summary>
/// Audit-log row for the Phase 06-shipped <c>bifrost.mc.v1/mc.command.#</c>
/// stream. Every orchestrator-processed <c>McCommand</c> — accepted or
/// rejected — produces one of these per CONTEXT D-23. <see cref="ResultJson"/>
/// is the recorder-synthesised composite of (success, message, new_state)
/// from the source <c>McCommandLogPayload</c>; rejected commands carry
/// <c>"success":false</c> and the rejection detail in <c>message</c> — they
/// are NOT dropped from the audit log (SPEC Req 10).
/// </summary>
public sealed record McCommandWrite(
    long TsNs,
    string Command,
    string ArgsJson,
    string ResultJson,
    string OperatorHostname,
    long ReceivedAtNs) : WriteCommand(ReceivedAtNs);
