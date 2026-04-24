namespace Bifrost.Imbalance;

/// <summary>
/// Seeded PRNG wrapping <see cref="System.Random"/> with a Marsaglia-polar Gaussian
/// sampler. Deterministic for any given seed: the same seed produces the same sequence
/// of both uniform and Gaussian draws across runs.
/// <para>
/// Uses only the seeded <see cref="System.Random"/> constructor — never
/// <c>Random.Shared</c> (banned by the lint fence). The polar method is in-tree (no
/// third-party dependency) and caches the second sample of each accepted pair, so the
/// average cost is one <c>NextDouble()</c> call per Gaussian draw.
/// </para>
/// </summary>
public sealed class SeededRandomSource : IRandomSource
{
    private Random _rng;
    private bool _hasSpareGaussian;
    private double _spareGaussian;

    /// <summary>
    /// Construct with an initial seed. The long is truncated to int (<see cref="System.Random"/>
    /// takes int); the truncation is deterministic so same-input → same-sequence across runs.
    /// </summary>
    public SeededRandomSource(long seed)
    {
        _rng = new Random(unchecked((int)seed));
    }

    /// <inheritdoc />
    public double NextDouble() => _rng.NextDouble();

    /// <inheritdoc />
    public double NextGaussian(double mean, double stdDev)
    {
        if (_hasSpareGaussian)
        {
            _hasSpareGaussian = false;
            return mean + stdDev * _spareGaussian;
        }

        double u, v, s;
        do
        {
            u = _rng.NextDouble() * 2.0 - 1.0;
            v = _rng.NextDouble() * 2.0 - 1.0;
            s = u * u + v * v;
        }
        while (s >= 1.0 || s == 0.0);

        var factor = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spareGaussian = v * factor;
        _hasSpareGaussian = true;
        return mean + stdDev * (u * factor);
    }

    /// <inheritdoc />
    public void Reseed(long seed)
    {
        _rng = new Random(unchecked((int)seed));
        _hasSpareGaussian = false;
        _spareGaussian = 0.0;
    }
}
