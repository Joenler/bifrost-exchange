namespace Bifrost.Gateway.Load.Tests;

/// <summary>
/// Poisson inter-arrival generator. CLAUDE.md (and BannedSymbols.txt) bans
/// <c>System.Random.Shared</c>; this constructor takes an explicit seed so the
/// load harness is reproducible across runs (D-20: same seed → same arrival
/// schedule). Uses inverse-CDF sampling on the exponential distribution to
/// yield Poisson-process inter-arrival intervals at the configured mean rate.
/// </summary>
public sealed class PoissonScheduler
{
    private readonly Random _rng;
    private readonly double _meanIntervalMs;

    public PoissonScheduler(int seed, int targetRatePerSecond)
    {
        if (targetRatePerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRatePerSecond));
        }

        _rng = new Random(seed);
        _meanIntervalMs = 1000.0 / targetRatePerSecond;
    }

    /// <summary>
    /// Returns the next inter-arrival interval, sampled from
    /// <c>Exponential(λ = targetRatePerSecond / 1000ms)</c> via inverse-CDF.
    /// </summary>
    public TimeSpan NextInterArrival()
    {
        // u in (0,1] (avoid log(0) — NextDouble() returns [0,1) so flip to (0,1]).
        var u = 1.0 - _rng.NextDouble();
        var ms = -_meanIntervalMs * Math.Log(u);
        return TimeSpan.FromMilliseconds(ms);
    }
}
