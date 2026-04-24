using Bifrost.Quoter.Pricing;

namespace Bifrost.Quoter.Schedule;

/// <summary>
/// Hybrid scenario-beats + Markov-overlay + MC-force regime FSM driving the
/// quoter's per-tick (drift, vol, kappa, spread/quantity multipliers) bundle.
/// <para>
/// Three rules govern <see cref="Advance"/>, applied in order:
/// <list type="number">
///   <item>If the current time has crossed into a new beat, hard-reset to that
///         beat's regime and clear any active MC force.</item>
///   <item>If an MC force is currently active, suppress Markov draws so the
///         operator-installed regime sticks until the next beat boundary.</item>
///   <item>Otherwise, perform a Markov draw against the per-second transition
///         rates using the exponential holding-time approximation
///         (p ≈ λ · dt for small λ · dt).</item>
/// </list>
/// </para>
/// <para>
/// Determinism: the Markov RNG is constructed from the scenario seed XOR'd
/// with a hard-coded salt so the schedule and the per-instrument GBM RNGs do
/// not share a stream and replay is bit-for-bit reproducible.
/// </para>
/// </summary>
public sealed class RegimeSchedule
{
    private readonly Scenario _scenario;
    // CRITICAL: 0xC0DECAFE is the documented Markov salt -- changing it breaks
    // the cross-build determinism contract. The XOR keeps the Markov stream
    // independent of the per-instrument GBM streams (which use a Knuth-style
    // multiplier with a different constant).
    private readonly Random _markovRng;
    private readonly DateTimeOffset _roundStartUtc;
    private readonly LruSet<Guid> _seenNonces = new(capacity: 16);
    private Beat _currentBeat;
    private Regime _currentRegime;
    private bool _mcForceActive;

    public RegimeSchedule(Scenario scenario, DateTimeOffset roundStartUtc)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Beats is null || scenario.Beats.Count == 0)
            throw new ArgumentException("Scenario must have at least one beat.", nameof(scenario));

        _scenario = scenario;
        // 0xC0DECAFE -- documented Markov salt, do not change.
        _markovRng = new Random(unchecked(scenario.Seed ^ (int)0xC0DECAFE));
        _roundStartUtc = roundStartUtc;
        _currentBeat = scenario.Beats[0];
        _currentRegime = _currentBeat.Regime;
        _mcForceActive = false;
    }

    /// <summary>Current regime as of the most recent <see cref="Advance"/> call.</summary>
    public Regime Current => _currentRegime;

    /// <summary>Returns the current regime's full <see cref="RegimeParams"/> bundle.</summary>
    public RegimeParams CurrentParams() => _scenario.RegimeParams[_currentRegime];

    /// <summary>Returns the GBM (drift, vol) pair for the current regime.</summary>
    public GbmParams CurrentGbmParams()
    {
        var rp = CurrentParams();
        return new GbmParams(rp.GbmDrift, rp.GbmVol);
    }

    /// <summary>
    /// Advances the FSM to <paramref name="now"/>. Returns the resulting
    /// transition (or <c>null</c> when the regime is unchanged).
    /// </summary>
    public RegimeTransition? Advance(DateTimeOffset now)
    {
        var t = (now - _roundStartUtc).TotalSeconds;
        var beatAtT = FindBeat(t);

        // Rule 1: beat boundary crossed -> hard reset, clear MC force.
        if (!ReferenceEquals(beatAtT, _currentBeat))
        {
            var from = _currentRegime;
            _currentBeat = beatAtT;
            _currentRegime = beatAtT.Regime;
            _mcForceActive = false;
            return from != _currentRegime
                ? new RegimeTransition(from, _currentRegime, McForced: false, TransitionReason.BeatBoundary)
                : null;
        }

        // Rule 2: MC force suppresses Markov until the next beat boundary.
        if (_mcForceActive)
            return null;

        // Rule 3: Markov draw at dt = 0.5 s tick.
        if (TryDrawMarkovTransition(dt: 0.5, out var newRegime))
        {
            var from = _currentRegime;
            _currentRegime = newRegime;
            return new RegimeTransition(from, newRegime, McForced: false, TransitionReason.Markov);
        }

        return null;
    }

    /// <summary>
    /// Installs an MC-force regime override. Idempotent across nonces: the same
    /// nonce processed twice returns <c>null</c> on the second call without
    /// modifying state. The override persists until the next beat boundary.
    /// </summary>
    public RegimeTransition? InstallMcForce(Regime forced, Guid nonce)
    {
        if (!_seenNonces.Add(nonce))
            return null;

        var from = _currentRegime;
        _currentRegime = forced;
        _mcForceActive = true;
        return new RegimeTransition(from, forced, McForced: true, TransitionReason.McForce);
    }

    private Beat FindBeat(double tSec)
    {
        // Linear scan from the back -- Beats list is small (typically 3-6).
        // Returns the active beat at time t. If t is before the first beat,
        // returns the first beat. If t is past the last beat's end, returns
        // the last beat (no transition fires past the end of the scenario).
        for (var i = _scenario.Beats.Count - 1; i >= 0; i--)
        {
            if (tSec >= _scenario.Beats[i].TOffsetSeconds)
                return _scenario.Beats[i];
        }
        return _scenario.Beats[0];
    }

    private bool TryDrawMarkovTransition(double dt, out Regime newRegime)
    {
        newRegime = _currentRegime;
        if (!_scenario.MarkovOverlay.TransitionRatesPerSecond.TryGetValue(_currentRegime, out var outRates))
            return false;

        // Sum of out-rates for the current regime; p(any transition this tick)
        // is approximately totalRate * dt for small λ · dt.
        var totalRate = 0.0;
        foreach (var (_, rate) in outRates)
            totalRate += rate;

        if (totalRate <= 0.0)
            return false;

        var pTransition = totalRate * dt;
        var u = _markovRng.NextDouble();
        if (u >= pTransition)
            return false;

        // Pick which target by cumulative-rate inversion. Iteration order is
        // dictionary insertion order -- for the System.Text.Json-loaded scenario
        // this is the JSON key order, which is stable for replay across builds.
        var u2 = _markovRng.NextDouble() * totalRate;
        var cum = 0.0;
        foreach (var (target, rate) in outRates)
        {
            cum += rate;
            if (u2 < cum)
            {
                newRegime = target;
                return true;
            }
        }

        return false;
    }
}
