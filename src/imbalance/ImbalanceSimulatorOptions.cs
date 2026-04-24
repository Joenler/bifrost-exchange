namespace Bifrost.Imbalance;

/// <summary>
/// Typed binding target for the ImbalanceSimulator section of config/hackathon.json.
/// All fields have defaults matching the imbalance pricing model reference values;
/// config overrides land via IOptions&lt;ImbalanceSimulatorOptions&gt; at boot.
/// </summary>
public sealed class ImbalanceSimulatorOptions
{
    /// <summary>Forecast publication cadence (seconds). Default 15.</summary>
    public int TForecastSeconds { get; set; } = 15;

    /// <summary>Rolloff window for transient physical shocks (seconds). Default 30.</summary>
    public int TTransientSeconds { get; set; } = 30;

    /// <summary>Forecast noise sigma at RoundOpen (EUR/MWh). Default 20.</summary>
    public double SigmaZeroEuroMwh { get; set; } = 20.0;

    /// <summary>Gate-time noise sigma for realized P_imb + forecast endpoint (EUR/MWh). Default 1.</summary>
    public double SigmaGateEuroMwh { get; set; } = 1.0;

    /// <summary>Penalty scale factor K in f(A) = -K * sign(A) * (|A|/N)^alpha. Default 50.</summary>
    public double K { get; set; } = 50.0;

    /// <summary>Penalty exponent alpha. Default 1.0 (linear); set &gt;1 for convex penalty.</summary>
    public double Alpha { get; set; } = 1.0;

    /// <summary>Normalization scale N (MWh) so |A|/N is typically order 1 in a round. Default 100.</summary>
    public double NScalingMwh { get; set; } = 100.0;

    /// <summary>Regime multiplier for Calm (reference regime). Default 1.0.</summary>
    public double GammaCalm { get; set; } = 1.0;

    /// <summary>Regime multiplier for Trending. Default 1.5.</summary>
    public double GammaTrending { get; set; } = 1.5;

    /// <summary>Regime multiplier for Volatile. Default 2.5.</summary>
    public double GammaVolatile { get; set; } = 2.5;

    /// <summary>Regime multiplier for Shock. Default 5.0.</summary>
    public double GammaShock { get; set; } = 5.0;

    /// <summary>Reference price S_q per quarter (ticks). Must have length 4.</summary>
    public long[] SReferenceTicksPerQuarter { get; set; } = new long[] { 50_000L, 52_000L, 54_000L, 53_000L };

    /// <summary>Ticks per EUR conversion factor. Default 100.</summary>
    public int TicksPerEuro { get; set; } = 100;

    /// <summary>Scenario seed for PRNG reseeding on each RoundOpen (XOR'd with round number).</summary>
    public long ScenarioSeed { get; set; } = 42L;

    /// <summary>Default regime at round start when no upstream regime source is wired.</summary>
    public string DefaultRegime { get; set; } = "Calm";

    /// <summary>Client ids excluded from settlement emission (e.g. quoter, dah-auction).</summary>
    public string[] NonSettlementClientIds { get; set; } = new[] { "quoter", "dah-auction" };

    /// <summary>Total round duration (seconds). Drives linear forecast-noise decay.</summary>
    public int RoundDurationSeconds { get; set; } = 600;
}
