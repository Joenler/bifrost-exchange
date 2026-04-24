using Bifrost.Quoter.Pricing;

namespace Bifrost.Quoter.Schedule;

/// <summary>
/// Loaded scenario definition. Drives the regime FSM (beats + Markov overlay)
/// and the per-regime knob bundle that flows into the GBM step + half-spread
/// calculation. Authored as a JSON file and parsed by <see cref="ScenarioLoader"/>;
/// the JSON Schema sibling (scenario.schema.json) documents the on-disk shape.
/// </summary>
public sealed record Scenario(
    string ScenarioId,
    string Description,
    int Seed,
    IReadOnlyList<Beat> Beats,
    MarkovOverlay MarkovOverlay,
    IReadOnlyDictionary<Regime, RegimeParams> RegimeParams);

/// <summary>
/// One scripted regime segment. <paramref name="TOffsetSeconds"/> is measured
/// from the round-start UTC anchor passed into <see cref="RegimeSchedule"/>.
/// </summary>
public sealed record Beat(double TOffsetSeconds, Regime Regime, double DurationSeconds);

/// <summary>
/// Per-second Markov transition rates layered on top of the deterministic
/// beat schedule. <c>TransitionRatesPerSecond[from][to]</c> is the rate λ used
/// in the exponential holding-time approximation; the chance of a transition
/// in a single tick of length dt is approximately λ · dt.
/// </summary>
public sealed record MarkovOverlay(
    IReadOnlyDictionary<Regime, IReadOnlyDictionary<Regime, double>> TransitionRatesPerSecond);

/// <summary>
/// Regime states. Values are locked to match the contracts-layer protobuf enum
/// (events.proto::Regime) so the publisher boundary maps 1:1 without translation.
/// Do NOT renumber.
/// </summary>
public enum Regime
{
    Unspecified = 0,
    Calm = 1,
    Trending = 2,
    Volatile = 3,
    Shock = 4
}

/// <summary>
/// Why the FSM produced a transition. Used by the publisher to label the
/// outbound RegimeChange event.
/// </summary>
public enum TransitionReason
{
    BeatBoundary,
    Markov,
    McForce
}

/// <summary>
/// Diff between two consecutive regime states. <see cref="McForced"/> distinguishes
/// operator-initiated transitions from scheduled ones for downstream display
/// and replay tooling.
/// </summary>
public readonly record struct RegimeTransition(Regime From, Regime To, bool McForced, TransitionReason Reason);

/// <summary>
/// Inbound message published by the MC-force consumer. Drained by the quoter
/// tick loop into <see cref="RegimeSchedule.InstallMcForce"/>.
/// </summary>
public sealed record RegimeForceMessage(Regime Regime, Guid Nonce);
