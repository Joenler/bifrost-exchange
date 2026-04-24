using System.Linq;
using Xunit;

namespace Bifrost.Imbalance.Tests;

public class SeededRandomSourceTests
{
    [Fact]
    public void SameSeed_ProducesIdenticalUniformSequence()
    {
        var a = new SeededRandomSource(42L);
        var b = new SeededRandomSource(42L);

        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextDouble(), b.NextDouble());
        }
    }

    [Fact]
    public void SameSeed_ProducesIdenticalGaussianSequence()
    {
        var a = new SeededRandomSource(42L);
        var b = new SeededRandomSource(42L);

        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextGaussian(0.0, 1.0), b.NextGaussian(0.0, 1.0));
        }
    }

    [Fact]
    public void Gaussian_MeanAndStdDev_ConvergeOverLargeSample()
    {
        var rng = new SeededRandomSource(42L);
        var samples = new double[20_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = rng.NextGaussian(mean: 5.0, stdDev: 2.0);
        }

        var mean = samples.Average();
        var variance = samples.Sum(x => (x - mean) * (x - mean)) / samples.Length;
        var stdDev = Math.Sqrt(variance);

        // Law of large numbers: 20k samples converge mean + stdDev to within 0.1 of target.
        Assert.True(Math.Abs(mean - 5.0) < 0.1, $"mean {mean} deviates more than 0.1 from 5.0");
        Assert.True(Math.Abs(stdDev - 2.0) < 0.1, $"stdDev {stdDev} deviates more than 0.1 from 2.0");
    }

    [Fact]
    public void Reseed_ResetsSequenceToNewSeed()
    {
        var rng = new SeededRandomSource(42L);
        var firstDouble = rng.NextDouble();
        var firstGaussian = rng.NextGaussian(0.0, 1.0);

        rng.Reseed(42L);
        var secondDouble = rng.NextDouble();
        var secondGaussian = rng.NextGaussian(0.0, 1.0);

        Assert.Equal(firstDouble, secondDouble);
        Assert.Equal(firstGaussian, secondGaussian);
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentSequences()
    {
        var a = new SeededRandomSource(42L);
        var b = new SeededRandomSource(43L);

        // At least one of the first 10 samples must differ (seed sensitivity).
        var anyDifferent = false;
        for (var i = 0; i < 10; i++)
        {
            if (a.NextDouble() != b.NextDouble())
            {
                anyDifferent = true;
                break;
            }
        }

        Assert.True(anyDifferent, "Seeds 42 and 43 produced identical first-10 uniforms");
    }

    [Fact]
    public void ReseedClearsSpareGaussianCache()
    {
        // Draw one Gaussian: polar method computes two and caches the spare. Re-seeding
        // must discard the cached spare so the next draw is a fresh sample from the new
        // seed's stream, not the stale cache from the old seed.
        var rng = new SeededRandomSource(42L);
        _ = rng.NextGaussian(0.0, 1.0);    // first call: computes 2 samples, caches spare

        rng.Reseed(42L);
        var afterReseed = rng.NextGaussian(0.0, 1.0);

        // After re-seeding with the same seed, the first Gaussian must equal the very
        // first Gaussian drawn on a fresh same-seed instance (cache must have been cleared).
        var fresh = new SeededRandomSource(42L).NextGaussian(0.0, 1.0);
        Assert.Equal(fresh, afterReseed);
    }
}
