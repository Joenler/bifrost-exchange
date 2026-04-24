using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Schedule;
using Xunit;

namespace Bifrost.Quoter.Tests.Schedule;

/// <summary>
/// Unit tests for <see cref="RegimeSchedule"/>. Covers the FSM determinism
/// contract, beat-boundary precedence over the Markov overlay, MC-force
/// install / release semantics, and statistical sanity of the Markov draw.
/// </summary>
public sealed class RegimeScheduleTests
{
    private static readonly DateTimeOffset RoundStart =
        new(2026, 4, 24, 10, 0, 0, TimeSpan.Zero);

    private static readonly string TestScenariosDir = Path.Combine(
        AppContext.BaseDirectory, "TestScenarios");

    private static Scenario LoadFixture(string fileName) =>
        ScenarioLoader.Load(Path.Combine(TestScenariosDir, fileName));

    private static readonly IReadOnlyDictionary<Regime, RegimeParams> StandardParams =
        new Dictionary<Regime, RegimeParams>
        {
            [Regime.Calm]     = new RegimeParams(1.0, 1.0, 0.0,  0.02, 1.5),
            [Regime.Trending] = new RegimeParams(1.2, 1.0, 0.05, 0.03, 1.0),
            [Regime.Volatile] = new RegimeParams(2.0, 0.6, 0.0,  0.08, 0.5),
            [Regime.Shock]    = new RegimeParams(4.0, 0.3, 0.0,  0.15, 0.2),
        };

    [Fact]
    public void Determinism_TwoSchedulesSameSeed_ProduceIdenticalTransitionSequence()
    {
        var s1 = LoadFixture("regime-sweep.json");
        var s2 = LoadFixture("regime-sweep.json");

        var a = new RegimeSchedule(s1, RoundStart);
        var b = new RegimeSchedule(s2, RoundStart);

        var transitionsA = new List<RegimeTransition>();
        var transitionsB = new List<RegimeTransition>();

        // 600 simulated seconds at 0.5 s ticks = 1200 steps.
        for (var i = 1; i <= 1200; i++)
        {
            var now = RoundStart.AddMilliseconds(i * 500);
            var ta = a.Advance(now);
            var tb = b.Advance(now);
            if (ta is not null) transitionsA.Add(ta.Value);
            if (tb is not null) transitionsB.Add(tb.Value);
        }

        Assert.Equal(transitionsA.Count, transitionsB.Count);
        Assert.True(transitionsA.Count > 0, "Expected at least the beat-boundary transitions to fire.");
        for (var i = 0; i < transitionsA.Count; i++)
            Assert.Equal(transitionsA[i], transitionsB[i]);
    }

    [Fact]
    public void Determinism_DifferentSeeds_DivergeWithinManySteps()
    {
        // Use the volatile-opening scenario whose Markov rates (0.001 .. 0.008)
        // are high enough to fire many times across 1200 ticks; toggling one
        // bit of the seed should produce a visibly different stream.
        var baseScenario = LoadFixture("volatile-opening.json");
        var divergent = baseScenario with { Seed = baseScenario.Seed ^ 1 };

        var a = new RegimeSchedule(baseScenario, RoundStart);
        var b = new RegimeSchedule(divergent, RoundStart);

        var transitionsA = new List<RegimeTransition>();
        var transitionsB = new List<RegimeTransition>();

        for (var i = 1; i <= 1200; i++)
        {
            var now = RoundStart.AddMilliseconds(i * 500);
            var ta = a.Advance(now);
            var tb = b.Advance(now);
            if (ta is not null) transitionsA.Add(ta.Value);
            if (tb is not null) transitionsB.Add(tb.Value);
        }

        // Beat-boundary transitions still match (deterministic schedule), but
        // overall sequences should differ by at least one Markov firing.
        var anyDifference = transitionsA.Count != transitionsB.Count;
        if (!anyDifference)
        {
            for (var i = 0; i < transitionsA.Count; i++)
            {
                if (!transitionsA[i].Equals(transitionsB[i])) { anyDifference = true; break; }
            }
        }
        Assert.True(anyDifference, "Expected Markov overlay to diverge between distinct seeds.");
    }

    [Fact]
    public void BeatBoundary_TakesPrecedenceOverMarkov()
    {
        // Beat 0 is CALM 0..1s with an absurdly high Markov rate to TRENDING.
        // Beat 1 is SHOCK starting at 1s. Crossing the boundary must fire a
        // BeatBoundary transition into SHOCK rather than a Markov transition
        // into TRENDING, even though the Markov rate would otherwise dominate.
        var scenario = new Scenario(
            ScenarioId: "beat-precedence",
            Description: "boundary-vs-markov precedence",
            Seed: 1,
            Beats: new[]
            {
                new Beat(0.0, Regime.Calm, 1.0),
                new Beat(1.0, Regime.Shock, 60.0)
            },
            MarkovOverlay: new MarkovOverlay(new Dictionary<Regime, IReadOnlyDictionary<Regime, double>>
            {
                [Regime.Calm] = new Dictionary<Regime, double> { [Regime.Trending] = 1000.0 }
            }),
            RegimeParams: StandardParams);

        var sched = new RegimeSchedule(scenario, RoundStart);

        // Advance to 0.5 s (still in beat 0). Markov may fire CALM -> TRENDING.
        var t0 = sched.Advance(RoundStart.AddMilliseconds(500));
        // Advance well past the boundary at t = 1.0 s.
        var t1 = sched.Advance(RoundStart.AddSeconds(1.5));

        Assert.NotNull(t1);
        Assert.Equal(TransitionReason.BeatBoundary, t1!.Value.Reason);
        Assert.Equal(Regime.Shock, t1.Value.To);
        Assert.False(t1.Value.McForced);
        // t0 is permitted to be either null or a Markov transition; the contract
        // only requires that the boundary crossing produces a BeatBoundary.
        _ = t0;
    }

    [Fact]
    public void BeatBoundary_ClearsMcForce()
    {
        // Beat 0: CALM 0..1 s. Beat 1: CALM 1..60 s (same regime, so the
        // boundary itself does not produce a transition diff). Install an MC
        // force to SHOCK at t = 0.5 s. The boundary at t = 1 s must clear the
        // MC-active flag, restoring the beat's CALM regime.
        var scenario = new Scenario(
            ScenarioId: "boundary-clears-mc",
            Description: "beat boundary clears MC-force flag",
            Seed: 7,
            Beats: new[]
            {
                new Beat(0.0, Regime.Calm, 1.0),
                new Beat(1.0, Regime.Calm, 60.0)
            },
            MarkovOverlay: new MarkovOverlay(new Dictionary<Regime, IReadOnlyDictionary<Regime, double>>
            {
                [Regime.Calm] = new Dictionary<Regime, double>()
            }),
            RegimeParams: StandardParams);

        var sched = new RegimeSchedule(scenario, RoundStart);
        var installed = sched.InstallMcForce(Regime.Shock, Guid.NewGuid());
        Assert.NotNull(installed);
        Assert.Equal(Regime.Shock, sched.Current);

        var crossBoundary = sched.Advance(RoundStart.AddSeconds(1.5));

        // Crossing the boundary back into CALM must produce a SHOCK -> CALM
        // BeatBoundary transition (proves the MC force was cleared and the
        // beat's regime won).
        Assert.NotNull(crossBoundary);
        Assert.Equal(TransitionReason.BeatBoundary, crossBoundary!.Value.Reason);
        Assert.Equal(Regime.Shock, crossBoundary.Value.From);
        Assert.Equal(Regime.Calm, crossBoundary.Value.To);
        Assert.False(crossBoundary.Value.McForced);
        Assert.Equal(Regime.Calm, sched.Current);
    }

    [Fact]
    public void InstallMcForce_ChangesRegimeImmediately_AndSuppressesMarkov()
    {
        // CALM beat with extremely high Markov rate. After installing an MC
        // force to VOLATILE, subsequent Advance calls within the same beat
        // must return null (Markov suppressed), and Current must be VOLATILE.
        var scenario = new Scenario(
            ScenarioId: "mc-suppresses-markov",
            Description: "MC force suppresses Markov until beat boundary",
            Seed: 99,
            Beats: new[]
            {
                new Beat(0.0, Regime.Calm, 600.0)
            },
            MarkovOverlay: new MarkovOverlay(new Dictionary<Regime, IReadOnlyDictionary<Regime, double>>
            {
                [Regime.Calm]     = new Dictionary<Regime, double> { [Regime.Trending] = 1000.0 },
                [Regime.Volatile] = new Dictionary<Regime, double> { [Regime.Shock]    = 1000.0 }
            }),
            RegimeParams: StandardParams);

        var sched = new RegimeSchedule(scenario, RoundStart);

        var installed = sched.InstallMcForce(Regime.Volatile, Guid.NewGuid());
        Assert.NotNull(installed);
        Assert.True(installed!.Value.McForced);
        Assert.Equal(Regime.Volatile, installed.Value.To);
        Assert.Equal(Regime.Volatile, sched.Current);

        // Many ticks later, all suppressed.
        for (var i = 1; i <= 50; i++)
        {
            var t = sched.Advance(RoundStart.AddMilliseconds(i * 500));
            Assert.Null(t);
        }
        Assert.Equal(Regime.Volatile, sched.Current);
    }

    [Fact]
    public void InstallMcForce_DuplicateNonce_ReturnsNullAndPreservesState()
    {
        var scenario = new Scenario(
            ScenarioId: "mc-nonce-dedup",
            Description: "duplicate MC nonce is a no-op",
            Seed: 0,
            Beats: new[] { new Beat(0.0, Regime.Calm, 600.0) },
            MarkovOverlay: new MarkovOverlay(new Dictionary<Regime, IReadOnlyDictionary<Regime, double>>
            {
                [Regime.Calm] = new Dictionary<Regime, double>()
            }),
            RegimeParams: StandardParams);

        var sched = new RegimeSchedule(scenario, RoundStart);
        var nonce = Guid.NewGuid();

        var first = sched.InstallMcForce(Regime.Shock, nonce);
        Assert.NotNull(first);
        Assert.Equal(Regime.Shock, sched.Current);

        // Second install with the same nonce must be a no-op even if the
        // requested regime differs.
        var second = sched.InstallMcForce(Regime.Volatile, nonce);
        Assert.Null(second);
        Assert.Equal(Regime.Shock, sched.Current);
    }

    [Fact]
    public void MarkovOverlay_HighRateOverManySteps_ApproximatelyMatchesExpectedFrequency()
    {
        // Two-state symmetric chain with rate λ from each side. Per-tick
        // transition probability is p = λ · dt; in equilibrium the chain
        // spends π = 0.5 of its time in each state. Expected count of
        // CALM -> TRENDING transitions over N ticks is therefore N · π · p.
        const double lambda = 0.1;
        const double dt = 0.5;
        const int totalSteps = 1000;
        const double equilibrium = 0.5;
        var expected = totalSteps * equilibrium * lambda * dt; // = 25

        var scenario = new Scenario(
            ScenarioId: "markov-stat",
            Description: "Markov rate statistical sanity",
            Seed: 31337,
            Beats: new[] { new Beat(0.0, Regime.Calm, 100000.0) },
            MarkovOverlay: new MarkovOverlay(new Dictionary<Regime, IReadOnlyDictionary<Regime, double>>
            {
                [Regime.Calm]     = new Dictionary<Regime, double> { [Regime.Trending] = lambda },
                [Regime.Trending] = new Dictionary<Regime, double> { [Regime.Calm]     = lambda }
            }),
            RegimeParams: StandardParams);

        var sched = new RegimeSchedule(scenario, RoundStart);

        var calmToTrending = 0;
        for (var i = 1; i <= totalSteps; i++)
        {
            var t = sched.Advance(RoundStart.AddMilliseconds(i * 500));
            if (t is not null && t.Value.From == Regime.Calm && t.Value.To == Regime.Trending)
                calmToTrending++;
        }

        // Allow ±40% slack -- single seed, finite trials, Poisson stdev sqrt(25) = 5.
        // ±40% (10 absolute) gives ~2-sigma headroom so this is not flaky.
        var lower = expected * 0.6;
        var upper = expected * 1.4;
        Assert.InRange(calmToTrending, (int)Math.Floor(lower), (int)Math.Ceiling(upper));
    }
}
