using Bifrost.Quoter.Tests.Fixtures;
using Xunit;

namespace Bifrost.Quoter.Tests.Integration;

/// <summary>
/// QTR-01 phase gate: the flagship determinism test. Two runs of the same
/// scenario seed must produce a byte-identical outbound command stream
/// (rolling SHA-256 over captured envelope payloads). A 1-bit-flip on the
/// seed against a scenario whose Markov overlay actually fires must produce
/// a different hash.
///
/// The same-seed test compares hashA == hashB across both fixtures; it does
/// NOT hardcode an expected hash value, so any legitimate code change that
/// re-orders or re-shapes commands updates both sides identically and the
/// test stays drift-resistant.
///
/// The seed-divergence test uses volatile-opening only because:
///   * calm-drift has an empty Markov overlay (no schedule transitions
///     after t=0), so the schedule's seed-XOR'd Markov RNG never draws.
///   * The GBM model's per-instrument RNG advances differently per seed but
///     its output is consumed by ComputeFairValue which currently returns
///     the constant truth (no microprice blend until BookView lands), so
///     the GBM stream does not affect captured order prices in this wave.
///   * volatile-opening has Markov rates (0.001 .. 0.008/s) high enough to
///     fire several transitions across the test horizon, and each Markov
///     transition produces a captured RegimeChange envelope plus a
///     cancel-and-requote burst, so a 1-bit seed flip is observable.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class DeterminismTests
{
    [Theory]
    [InlineData("volatile-opening.json", 424242)]
    [InlineData("calm-drift.json", 777)]
    public async Task Determinism_SameSeedSameRound_ProducesIdenticalCommandStream(string scenarioFile, int seed)
    {
        var hashA = await CaptureRunHashAsync(scenarioFile, seed);
        var hashB = await CaptureRunHashAsync(scenarioFile, seed);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public async Task Determinism_DifferentSeed_ProducesDifferentCommandStream()
    {
        const int seed = 424242;
        const string scenarioFile = "volatile-opening.json";

        var hashA = await CaptureRunHashAsync(scenarioFile, seed);
        var hashFlipped = await CaptureRunHashAsync(scenarioFile, seed ^ 1);

        Assert.NotEqual(hashA, hashFlipped);
    }

    private static async Task<string> CaptureRunHashAsync(string scenarioFile, int seed)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost(scenarioFile, overrideSeed: seed);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(60.0);
        await host.Quoter.StopAsync(ct);
        return host.TestPublisher.RollingSha256Hex;
    }
}
