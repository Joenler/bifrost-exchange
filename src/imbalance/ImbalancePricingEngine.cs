using Microsoft.Extensions.Options;

namespace Bifrost.Imbalance;

/// <summary>
/// Pure-function imbalance pricing engine. Implements the aggregate-position single
/// imbalance-price formula:
/// <code>
///   P_imb_q = S_q + round(gamma_regime * f(A_total_q) * ticks_per_euro)
///                 + round(epsilon * ticks_per_euro)
///   f(A)    = -K * sign(A) * (|A|/N_scaling)^alpha
/// </code>
/// over integer ticks. No IO, no time, no state — callers inject
/// <see cref="IRandomSource"/> so the noise stream is deterministic per round.
/// The final sum uses <c>checked</c> arithmetic; at default parameters
/// (K=50, alpha=1, N=100) with realistic aggregate imbalance no int64 overflow is
/// possible, but the guard catches future regressions from extreme tuning.
/// </summary>
public sealed class ImbalancePricingEngine
{
    private readonly IOptions<ImbalanceSimulatorOptions> _options;

    public ImbalancePricingEngine(IOptions<ImbalanceSimulatorOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Realized imbalance price at Gate for the given quarter (0..3). Called exactly four
    /// times per Gate — once per quarter.
    /// </summary>
    public long ComputePImbTicks(
        int quarterIndex,
        long aTeamsTicks,
        long aPhysicalTicks,
        string regime,
        IRandomSource rng)
    {
        if (quarterIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(quarterIndex), "quarter_index must be 0..3");
        }

        var o = _options.Value;
        var aTotalTicks = checked(aTeamsTicks + aPhysicalTicks);
        var aTotalMwh = aTotalTicks / (double)o.TicksPerEuro;

        var gamma = ResolveGamma(regime, o);
        var fAtotal = ComputeF(aTotalMwh, o);

        var epsilonEuro = rng.NextGaussian(mean: 0.0, stdDev: o.SigmaGateEuroMwh);

        var sTicks = o.SReferenceTicksPerQuarter[quarterIndex];
        var penaltyTicks = (long)Math.Round(gamma * fAtotal * o.TicksPerEuro);
        var noiseTicks = (long)Math.Round(epsilonEuro * o.TicksPerEuro);

        return checked(sTicks + penaltyTicks + noiseTicks);
    }

    /// <summary>
    /// Forecast-time imbalance-price estimate. Noise sigma decays linearly from
    /// <see cref="ImbalanceSimulatorOptions.SigmaZeroEuroMwh"/> at <c>t=0</c> to
    /// <see cref="ImbalanceSimulatorOptions.SigmaGateEuroMwh"/> at
    /// <c>t=RoundDurationSeconds</c>. Callers supply the active quarter index (which S_q
    /// reference to use) and the aggregate (A_teams + A_physical) snapshot at time t.
    /// </summary>
    public long ComputeForecastPriceTicks(
        double elapsedSecondsSinceRoundOpen,
        int activeQuarterIndex,
        long aTeamsTicks,
        long aPhysicalTicks,
        string regime,
        IRandomSource rng)
    {
        if (activeQuarterIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(activeQuarterIndex), "quarter_index must be 0..3");
        }

        var o = _options.Value;
        var fraction = o.RoundDurationSeconds <= 0
            ? 1.0
            : Math.Clamp(elapsedSecondsSinceRoundOpen / o.RoundDurationSeconds, 0.0, 1.0);
        var sigmaT = o.SigmaZeroEuroMwh + (o.SigmaGateEuroMwh - o.SigmaZeroEuroMwh) * fraction;

        var aTotalTicks = checked(aTeamsTicks + aPhysicalTicks);
        var aTotalMwh = aTotalTicks / (double)o.TicksPerEuro;
        var gamma = ResolveGamma(regime, o);
        var fAtotal = ComputeF(aTotalMwh, o);

        var noiseEuro = rng.NextGaussian(mean: 0.0, stdDev: sigmaT);

        var sTicks = o.SReferenceTicksPerQuarter[activeQuarterIndex];
        var penaltyTicks = (long)Math.Round(gamma * fAtotal * o.TicksPerEuro);
        var noiseTicks = (long)Math.Round(noiseEuro * o.TicksPerEuro);

        return checked(sTicks + penaltyTicks + noiseTicks);
    }

    private static double ComputeF(double aTotalMwh, ImbalanceSimulatorOptions o)
    {
        if (aTotalMwh == 0.0)
        {
            return 0.0;
        }

        return -o.K
             * Math.Sign(aTotalMwh)
             * Math.Pow(Math.Abs(aTotalMwh) / o.NScalingMwh, o.Alpha);
    }

    private static double ResolveGamma(string regime, ImbalanceSimulatorOptions o) => regime switch
    {
        "Calm" => o.GammaCalm,
        "Trending" => o.GammaTrending,
        "Volatile" => o.GammaVolatile,
        "Shock" => o.GammaShock,
        _ => o.GammaCalm,
    };
}
