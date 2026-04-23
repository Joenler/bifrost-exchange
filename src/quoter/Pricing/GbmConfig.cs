namespace Bifrost.Quoter.Pricing;

/// <summary>
/// Configuration parameters for the GBM price simulation model.
/// Adapted from Arena: <c>BaseVolatility</c> and <c>RegimeTransitionRate</c>
/// are removed because regime state (and therefore drift+vol) is now owned
/// externally and supplied to <see cref="GbmPriceModel.StepAll"/> via
/// <see cref="GbmParams"/>.
/// </summary>
public sealed record GbmConfig
{
    public long DefaultSeedPriceTicks { get; }
    public int Seed { get; }
    public double Dt { get; }

    public GbmConfig(
        long DefaultSeedPriceTicks,
        int Seed,
        double Dt = 1.0)
    {
        if (DefaultSeedPriceTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(DefaultSeedPriceTicks), DefaultSeedPriceTicks, "Must be positive.");

        if (Dt <= 0)
            throw new ArgumentOutOfRangeException(nameof(Dt), Dt, "Must be positive.");

        this.DefaultSeedPriceTicks = DefaultSeedPriceTicks;
        this.Seed = Seed;
        this.Dt = Dt;
    }
}
