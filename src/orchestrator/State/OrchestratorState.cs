using Bifrost.Exchange.Application.RoundState;

namespace Bifrost.Orchestrator.State;

/// <summary>
/// Persisted orchestrator state — the full snapshot written to disk on every
/// transition via <see cref="JsonStateStore"/>. The wire-side
/// <c>RoundStateChangedPayload</c> in <c>Bifrost.Contracts.Internal.Events</c>
/// carries a delta projection; the seven operator-internal fields
/// (MasterSeed, ScenarioSeedInternal, NextRoundNumber, EventOver,
/// ScoredRoundsCompleted, AbortReason, IterationSeedRotationCount local
/// snapshot) stay off the wire and live only in the on-disk snapshot.
/// Diff-viewable by an operator during a live event
/// (<c>cat /tmp/bifrost-orchestrator-state.json</c>).
/// </summary>
public sealed record OrchestratorState(
    int SchemaVersion,
    RoundState State,
    int RoundNumber,
    long ScenarioSeedInternal,
    bool Paused,
    string? PausedReason,
    bool Blocked,
    string? BlockedReason,
    long LastTransitionNs,
    int NextRoundNumber,
    long MasterSeed,
    int IterationSeedRotationCount,
    bool EventOver,
    int ScoredRoundsCompleted,
    string? AbortReason)
{
    /// <summary>
    /// On-disk schema version. Increment only when a breaking field change
    /// lands; non-breaking additions rely on <c>JsonSerializer</c>'s
    /// ignore-missing-properties behaviour.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Construct a fresh first-boot state at <see cref="RoundState.IterationOpen"/>
    /// with the given master seed. Used by the orchestrator actor when
    /// <see cref="JsonStateStore.TryLoad"/> returns null.
    /// </summary>
    public static OrchestratorState FreshBoot(long masterSeed, long nowNs) => new(
        SchemaVersion: CurrentSchemaVersion,
        State: RoundState.IterationOpen,
        RoundNumber: 0,
        ScenarioSeedInternal: 0L,
        Paused: false,
        PausedReason: null,
        Blocked: false,
        BlockedReason: null,
        LastTransitionNs: nowNs,
        NextRoundNumber: 1,
        MasterSeed: masterSeed,
        IterationSeedRotationCount: 0,
        EventOver: false,
        ScoredRoundsCompleted: 0,
        AbortReason: null);
}
