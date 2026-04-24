using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// IMB-07 / ADR-0003 linear-decay invariant test. Drives
/// <see cref="ImbalancePricingEngine.ComputeForecastPriceTicks"/> directly at
/// three sample points and runs a 200-sample Monte Carlo per point with
/// A_teams = A_physical = 0 (isolating the noise term from the penalty term)
/// to extract an empirical sample standard deviation of <c>p_ticks − S_q</c>
/// converted back into €/MWh. The sample σ must match the linear-decay
/// formula's expected σ(t) within ±0.5 €/MWh at each sample point:
/// <list type="bullet">
/// <item>t = 0 → σ(t) = σ_0 = 20.</item>
/// <item>t = T/2 → σ(t) = (σ_0 + σ_gate) / 2 = 10.5.</item>
/// <item>t = T → σ(t) = σ_gate = 1.</item>
/// </list>
/// <para>
/// Design choices:
/// </para>
/// <list type="bullet">
/// <item><c>K = 0</c> in the options so the f(A_total) penalty term
/// contributes exactly zero regardless of aggregates. That leaves only the
/// Gaussian noise component active — the exact quantity whose σ we want to
/// measure.</item>
/// <item>Per-sample seeding via <c>new SeededRandomSource(1000 + i)</c> gives
/// 2000 independent Gaussian draws per sample point. Each Gaussian is exactly
/// one sigma-scaled N(0,1) draw from the PRNG. Sample σ's standard error is
/// σ/√(2N); at N=2000 and σ=20 that is ≈ 0.316 €/MWh — comfortably inside the
/// ±0.5 tolerance with margin. For σ = 1 (t=T_round) the error shrinks to
/// ≈ 0.016 — the tolerance is dominated by rounding-to-ticks quantisation
/// (ticks_per_euro = 100, so the smallest observable delta is 0.01 €/MWh).</item>
/// </list>
/// </summary>
public class ForecastNoiseDecayTests
{
    [Theory]
    [InlineData(0.0, 20.0)]     // t=0 → σ = σ_0
    [InlineData(300.0, 10.5)]   // t=T/2 → σ = (σ_0 + σ_gate) / 2
    [InlineData(600.0, 1.0)]    // t=T → σ = σ_gate
    public void Linear_Decay_Matches_SigmaAtSampledPoints_Within0Point5EuroMwh(
        double elapsedSeconds, double expectedSigma)
    {
        const int samples = 2_000;
        const int ticksPerEuro = 100;
        const long sReferenceQ0 = 50_000L;

        var options = new ImbalanceSimulatorOptions
        {
            TForecastSeconds = 15,
            RoundDurationSeconds = 600,
            SigmaZeroEuroMwh = 20.0,
            SigmaGateEuroMwh = 1.0,

            // Zero out the penalty term so the Gaussian noise is the only
            // contribution to (p_ticks − S_q). K = 0 → f(A_total) = 0
            // regardless of the aTotal sign/magnitude.
            K = 0.0,
            Alpha = 1.0,
            NScalingMwh = 100.0,

            GammaCalm = 1.0,
            GammaTrending = 1.0,
            GammaVolatile = 1.0,
            GammaShock = 1.0,

            SReferenceTicksPerQuarter = new[] { sReferenceQ0, sReferenceQ0, sReferenceQ0, sReferenceQ0 },
            TicksPerEuro = ticksPerEuro,
            DefaultRegime = "Calm",
        };
        var engine = new ImbalancePricingEngine(Options.Create(options));

        var deltasEuro = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            // Per-sample PRNG. Each call consumes exactly one NextGaussian —
            // the first (and only) noise draw is σ(t) * N(0,1).
            var rng = new SeededRandomSource(1000L + i);
            var pTicks = engine.ComputeForecastPriceTicks(
                elapsedSecondsSinceRoundOpen: elapsedSeconds,
                activeQuarterIndex: 0,
                aTeamsTicks: 0L,
                aPhysicalTicks: 0L,
                regime: "Calm",
                rng: rng);
            deltasEuro[i] = (pTicks - sReferenceQ0) / (double)ticksPerEuro;
        }

        var mean = deltasEuro.Average();
        var variance = deltasEuro.Sum(x => (x - mean) * (x - mean)) / samples;
        var empiricalSigma = Math.Sqrt(variance);

        Assert.InRange(empiricalSigma, expectedSigma - 0.5, expectedSigma + 0.5);
    }

    /// <summary>
    /// Additional invariant assertion at the endpoints — σ(t=0) is strictly
    /// greater than σ(t=T_round) when σ_0 &gt; σ_gate. Catches a regression
    /// where the decay math flips sign or uses the endpoints in reverse.
    /// </summary>
    [Fact]
    public void Sigma_StrictlyMonotonicDecrease_FromStartToGate()
    {
        var options = new ImbalanceSimulatorOptions
        {
            RoundDurationSeconds = 600,
            SigmaZeroEuroMwh = 20.0,
            SigmaGateEuroMwh = 1.0,
            K = 0.0,
            SReferenceTicksPerQuarter = new long[] { 50_000L, 50_000L, 50_000L, 50_000L },
            TicksPerEuro = 100,
            DefaultRegime = "Calm",
        };
        var engine = new ImbalancePricingEngine(Options.Create(options));

        const int samples = 200;
        var deltasStart = new double[samples];
        var deltasGate = new double[samples];

        for (var i = 0; i < samples; i++)
        {
            var rngStart = new SeededRandomSource(2000L + i);
            var rngGate = new SeededRandomSource(2000L + i);

            var pStart = engine.ComputeForecastPriceTicks(
                elapsedSecondsSinceRoundOpen: 0.0,
                activeQuarterIndex: 0,
                aTeamsTicks: 0L,
                aPhysicalTicks: 0L,
                regime: "Calm",
                rng: rngStart);

            var pGate = engine.ComputeForecastPriceTicks(
                elapsedSecondsSinceRoundOpen: options.RoundDurationSeconds,
                activeQuarterIndex: 0,
                aTeamsTicks: 0L,
                aPhysicalTicks: 0L,
                regime: "Calm",
                rng: rngGate);

            deltasStart[i] = (pStart - 50_000L) / 100.0;
            deltasGate[i] = (pGate - 50_000L) / 100.0;
        }

        var sigmaStart = SampleSigma(deltasStart);
        var sigmaGate = SampleSigma(deltasGate);

        Assert.True(sigmaStart > sigmaGate,
            $"σ(t=0)={sigmaStart:F3} must exceed σ(t=T)={sigmaGate:F3} under linear decay σ_0=20 → σ_gate=1");
    }

    private static double SampleSigma(double[] xs)
    {
        var mean = xs.Average();
        var variance = xs.Sum(x => (x - mean) * (x - mean)) / xs.Length;
        return Math.Sqrt(variance);
    }
}
