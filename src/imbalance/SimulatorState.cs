using Bifrost.Exchange.Application.RoundState;

namespace Bifrost.Imbalance;

/// <summary>
/// Mutable simulator state container. Single-writer by construction: mutated only by
/// the actor drain loop, which sequentially pattern-matches the shared bounded channel.
/// No locks are needed because the drain loop is the sole mutator — producer hosted
/// services push onto the channel but never touch state directly. DO NOT read or mutate
/// from any thread other than the drain loop.
/// <para>
/// This is deliberately a plain <see cref="Dictionary{TKey, TValue}"/> rather than
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>:
/// concurrent dictionaries are the wrong tool for compound read-modify-write operations
/// against scoring-affecting state.
/// </para>
/// </summary>
public sealed class SimulatorState
{
    /// <summary>
    /// Net-position map keyed by (clientId, quarterIndex). QuarterIndex is 0..3; the
    /// hour-instrument fills never reach this map (they are dropped at the fill-consumer
    /// boundary via <see cref="QuarterIndexResolver"/>).
    /// </summary>
    public Dictionary<(string ClientId, int QuarterIndex), long> NetPositions { get; } = new();

    /// <summary>
    /// Per-quarter sum of currently-contributing physical shocks (round-persistent shocks
    /// plus transient shocks still inside their window). Indexed 0..3. Updated on every
    /// <see cref="ShockMessage"/> and on transient rolloff.
    /// </summary>
    public long[] APhysicalQh { get; } = new long[4];

    /// <summary>
    /// Currently-active transient shocks. Tracked individually so each contribution can be
    /// subtracted off its target quarter when its window elapses.
    /// </summary>
    public List<TransientShock> PendingTransients { get; } = new();

    /// <summary>
    /// Last round-state value observed by the drain loop. Defaults to IterationOpen; the
    /// round-state bridge updates this on every transition.
    /// </summary>
    public RoundState CurrentRoundState { get; set; } = RoundState.IterationOpen;

    /// <summary>
    /// Round number attached to every ImbalancePrint + ImbalanceSettlement emitted at
    /// Gate / Settled. Incremented by the drain loop when a fresh round opens.
    /// </summary>
    public int CurrentRoundNumber { get; set; }

    /// <summary>
    /// Currently-active regime name (Calm / Trending / Volatile / Shock). Drives the
    /// gamma multiplier in the pricing engine. Phase 04 reads the default from config;
    /// a later orchestrator phase may rewire this to live regime updates.
    /// </summary>
    public string CurrentRegime { get; set; } = "Calm";

    /// <summary>
    /// Clear all round-scoped accumulators on a new-round transition. Deliberately does
    /// NOT reset <see cref="CurrentRoundNumber"/> or <see cref="CurrentRegime"/> — those
    /// are updated explicitly by the drain loop from the incoming RoundStateMessage.
    /// </summary>
    public void ResetForNewRound()
    {
        NetPositions.Clear();
        Array.Clear(APhysicalQh, 0, APhysicalQh.Length);
        PendingTransients.Clear();
    }
}

/// <summary>
/// In-flight transient shock. Contributes <see cref="ContributionTicks"/> to
/// <see cref="SimulatorState.APhysicalQh"/>[<see cref="QuarterIndex"/>] while
/// (now − <see cref="ActivatedTsNs"/>) &lt; <see cref="TransientWindowNs"/>. Rolled off by
/// the drain loop when the window elapses.
/// </summary>
public sealed record TransientShock(
    long ActivatedTsNs,
    long TransientWindowNs,
    int QuarterIndex,
    long ContributionTicks);
