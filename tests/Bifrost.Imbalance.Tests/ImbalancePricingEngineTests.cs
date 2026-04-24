using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Imbalance.Tests;

public class ImbalancePricingEngineTests
{
    private static ImbalanceSimulatorOptions DefaultOptions(double sigmaGate = 0.0) => new()
    {
        K = 50.0,
        Alpha = 1.0,
        NScalingMwh = 100.0,
        GammaCalm = 1.0,
        GammaTrending = 1.5,
        GammaVolatile = 2.5,
        GammaShock = 5.0,
        SigmaZeroEuroMwh = 20.0,
        SigmaGateEuroMwh = sigmaGate,
        SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
        TicksPerEuro = 100,
        RoundDurationSeconds = 600,
    };

    [Fact]
    public void ComputePImbTicks_ZeroAggregate_NoNoise_ReturnsSq()
    {
        var engine = new ImbalancePricingEngine(Options.Create(DefaultOptions(sigmaGate: 0.0)));
        var rng = new SeededRandomSource(42L);

        Assert.Equal(50_000L, engine.ComputePImbTicks(0, 0L, 0L, "Calm", rng));
        Assert.Equal(52_000L, engine.ComputePImbTicks(1, 0L, 0L, "Calm", rng));
        Assert.Equal(54_000L, engine.ComputePImbTicks(2, 0L, 0L, "Calm", rng));
        Assert.Equal(53_000L, engine.ComputePImbTicks(3, 0L, 0L, "Calm", rng));
    }

    [Fact]
    public void GivenHandComputedFixture_EmitsFourPImb_WithinOneTick()
    {
        // Hand-computed fixture covering the SPEC acceptance example:
        //   A_physical on Q2 = -300 MWh → aPhysicalTicks = -30_000 (ticks-of-MWh at ticks_per_euro=100)
        //   regime = Volatile → gamma = 2.5
        //   sigma_gate = 0 → epsilon = 0
        //   f(A_total) = -K * sign(A) * (|A|/N)^alpha
        //              = -50 * sign(-300) * (300/100)^1
        //              = -50 * (-1) * 3.0
        //              = +150
        //   gamma * f = 2.5 * 150 = 375 EUR/MWh penalty
        //   penalty_ticks = round(375 * 100) = 37_500
        //   P_imb_ticks(Q2) = S_q(2) + penalty_ticks = 54_000 + 37_500 = 91_500
        var engine = new ImbalancePricingEngine(Options.Create(DefaultOptions(sigmaGate: 0.0)));
        var rng = new SeededRandomSource(42L);

        var pimb = engine.ComputePImbTicks(
            quarterIndex: 2,
            aTeamsTicks: 0L,
            aPhysicalTicks: -30_000L,
            regime: "Volatile",
            rng: rng);

        Assert.InRange(pimb, 91_500L - 1, 91_500L + 1);
    }

    [Fact]
    public void ComputePImbTicks_SystemLong_PushesPriceBelowReference()
    {
        // Sign check: A_total > 0 → f < 0 → penalty_ticks < 0 → P_imb < S_q.
        // aPhysicalTicks = +20_000 (= +200 MWh), Calm regime (gamma=1):
        //   f = -50 * 1 * (200/100)^1 = -100
        //   penalty_ticks = round(1 * -100 * 100) = -10_000
        //   P_imb = 50_000 + -10_000 = 40_000
        var engine = new ImbalancePricingEngine(Options.Create(DefaultOptions(sigmaGate: 0.0)));
        var rng = new SeededRandomSource(42L);

        var pimb = engine.ComputePImbTicks(
            quarterIndex: 0,
            aTeamsTicks: 0L,
            aPhysicalTicks: 20_000L,
            regime: "Calm",
            rng: rng);

        Assert.InRange(pimb, 40_000L - 1, 40_000L + 1);
    }

    [Fact]
    public void ComputePImbTicks_UnknownRegime_FallsBackToCalmGamma()
    {
        // Same -300 MWh fixture but regime="Unknown" should fall back to gamma_calm = 1.0.
        //   penalty_ticks = round(1 * 150 * 100) = 15_000
        //   P_imb = 54_000 + 15_000 = 69_000
        var engine = new ImbalancePricingEngine(Options.Create(DefaultOptions(sigmaGate: 0.0)));
        var rng = new SeededRandomSource(42L);

        var pimb = engine.ComputePImbTicks(
            quarterIndex: 2,
            aTeamsTicks: 0L,
            aPhysicalTicks: -30_000L,
            regime: "Unknown",
            rng: rng);

        Assert.InRange(pimb, 69_000L - 1, 69_000L + 1);
    }

    [Fact]
    public void ComputePImbTicks_QuarterIndexOutOfRange_Throws()
    {
        var engine = new ImbalancePricingEngine(Options.Create(DefaultOptions()));
        var rng = new SeededRandomSource(42L);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.ComputePImbTicks(4, 0L, 0L, "Calm", rng));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.ComputePImbTicks(-1, 0L, 0L, "Calm", rng));
    }

    [Fact]
    public void ComputeForecastPriceTicks_AtGateBoundary_SigmaGateZero_ReturnsSq()
    {
        // t = T_round → sigma(t) = sigma_gate = 0 → noise = 0.
        // A_total = 0 → f = 0 → penalty = 0. Result must equal S_q exactly.
        var engine = new ImbalancePricingEngine(Options.Create(DefaultOptions(sigmaGate: 0.0)));
        var rng = new SeededRandomSource(42L);

        var p = engine.ComputeForecastPriceTicks(
            elapsedSecondsSinceRoundOpen: 600.0,
            activeQuarterIndex: 0,
            aTeamsTicks: 0L,
            aPhysicalTicks: 0L,
            regime: "Calm",
            rng: rng);

        Assert.Equal(50_000L, p);
    }

    [Fact]
    public void ComputeForecastPriceTicks_AtRoundOpen_CarriesSigmaZero()
    {
        // t=0 → sigma(t) = sigma_zero (wide). We cannot assert a specific noise value
        // without assuming the Gaussian sample; the weaker assertion is that the forecast
        // equals S_q plus the first noise draw from the known-seed PRNG.
        // With sigma_zero=20, sigma_gate=0, A_total=0: penalty=0, only the noise term.
        var opts = DefaultOptions(sigmaGate: 0.0);
        opts.SigmaZeroEuroMwh = 20.0;
        var engine = new ImbalancePricingEngine(Options.Create(opts));

        // Draw the expected noise from a parallel same-seed RNG to derive the expected value.
        var referenceRng = new SeededRandomSource(42L);
        var expectedNoiseEuro = referenceRng.NextGaussian(0.0, 20.0);
        var expectedNoiseTicks = (long)Math.Round(expectedNoiseEuro * 100);

        var rng = new SeededRandomSource(42L);
        var p = engine.ComputeForecastPriceTicks(
            elapsedSecondsSinceRoundOpen: 0.0,
            activeQuarterIndex: 0,
            aTeamsTicks: 0L,
            aPhysicalTicks: 0L,
            regime: "Calm",
            rng: rng);

        Assert.Equal(50_000L + expectedNoiseTicks, p);
    }

    [Fact]
    public void ComputeForecastPriceTicks_QuarterIndexOutOfRange_Throws()
    {
        var engine = new ImbalancePricingEngine(Options.Create(DefaultOptions()));
        var rng = new SeededRandomSource(42L);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.ComputeForecastPriceTicks(0.0, 4, 0L, 0L, "Calm", rng));
    }
}
