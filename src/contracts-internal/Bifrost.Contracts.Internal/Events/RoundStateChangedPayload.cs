namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Wire DTO for publications on bifrost.round.v1 topic exchange, routing key
/// round.state.{state_snake}. The orchestrator is the sole producer;
/// exchange / quoter / imbalance / DAH / gateway / recorder / bigscreen consume.
/// </summary>
/// <remarks>
/// Fields mirror the delta-projection of the persisted orchestrator state (the
/// on-disk snapshot carries additional operator-internal fields that are NOT
/// placed on the wire: master_seed, scenario_seed_internal, event_over,
/// scored_rounds_completed, next_round_number).
/// IsReconciliation is true only for the single startup-publish a just-restarted
/// orchestrator fires.
/// ScenarioSeedOnWire is 0 during scored rounds (AuctionOpen/AuctionClosed/
/// RoundOpen/Gate/Settled/Aborted) and equals the current iteration seed during
/// IterationOpen.
/// </remarks>
public sealed record RoundStateChangedPayload(
    string State,
    int RoundNumber,
    long ScenarioSeedOnWire,
    long TransitionNs,
    long? ExpectedNextTransitionNs,
    bool Paused,
    string? PausedReason,
    bool Blocked,
    string? BlockedReason,
    bool IsReconciliation,
    int IterationSeedRotationCount,
    string? AbortReason);
