namespace Bifrost.Imbalance;

/// <summary>
/// PRNG abstraction the simulator's pricing engine and noise loops consume. An abstraction
/// keeps tests able to inject a predictable stream and keeps the composition-root helper
/// (constructed with scenario_seed XOR round_number on each RoundOpen) decoupled from the
/// callers that draw samples.
/// </summary>
public interface IRandomSource
{
    /// <summary>Uniform sample on [0, 1).</summary>
    double NextDouble();

    /// <summary>Gaussian sample with given mean and standard deviation.</summary>
    double NextGaussian(double mean, double stdDev);

    /// <summary>
    /// Reseed the PRNG. Called on every RoundOpen with the per-round seed (typically
    /// scenario_seed XOR round_number) to guarantee byte-identical replays.
    /// </summary>
    void Reseed(long seed);
}
