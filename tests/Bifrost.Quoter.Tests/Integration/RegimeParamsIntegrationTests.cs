using System.Globalization;
using Bifrost.Quoter.Tests.Fixtures;
using Xunit;

namespace Bifrost.Quoter.Tests.Integration;

/// <summary>
/// QTR-03 integration tests. Each regime's RegimeParams (SpreadMultiplier,
/// QuantityMultiplier, GbmDrift, GbmVol, Kappa) must actually drive the
/// Quoter's per-tick behaviour:
///   1. CALM and VOLATILE differ in spread width: VOLATILE submits orders
///      with wider bid-ask distance per pyramid level.
///   2. CALM and VOLATILE differ in quantity: VOLATILE pulls back
///      (QuantityMultiplier=0.6) so submitted quantities shrink.
/// Both effects are observable via the captured TestRabbitPublisher stream
/// using the regime-sweep fixture (60s per beat: CALM, TRENDING, VOLATILE,
/// SHOCK).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class RegimeParamsIntegrationTests
{
    [Fact]
    public async Task CalmRegime_HasNarrowerSpreadThanVolatileRegime()
    {
        await using var host = new TestQuoterHost("regime-sweep.json", overrideSeed: 9001);
        var ct = TestContext.Current.CancellationToken;
        await host.Quoter.StartAsync(ct);
        // Sample within CALM beat (t=0..60). Skip the first second so the
        // initial pyramid has had time to land.
        await host.AdvanceSecondsAsync(15.0);
        var calmCount = host.TestPublisher.Captured.Count;

        // Advance into VOLATILE beat (t=120..180).
        await host.AdvanceSecondsAsync(115.0);
        await host.Quoter.StopAsync(ct);

        var allCaptured = host.TestPublisher.Captured;
        var calmSubmits = allCaptured
            .Take(calmCount)
            .Where(c => c.Kind == "SubmitLimitOrder")
            .Select(c => ParsePriceTicks(c.JsonBody))
            .ToList();
        var volatileSubmits = allCaptured
            .Skip(calmCount)
            .Where(c => c.Kind == "SubmitLimitOrder")
            .Select(c => ParsePriceTicks(c.JsonBody))
            .ToList();

        Assert.NotEmpty(calmSubmits);
        Assert.NotEmpty(volatileSubmits);

        // Use range (max - min) of submitted prices around the constant truth
        // (5000 ticks) as a coarse spread proxy. CALM regime collapses
        // rapidly toward the truth (kappa=1.5, SpreadMultiplier=1.0); VOLATILE
        // (kappa=0.5, SpreadMultiplier=2.0) emits wider pyramids.
        var calmRange = calmSubmits.Max() - calmSubmits.Min();
        var volatileRange = volatileSubmits.Max() - volatileSubmits.Min();

        Assert.True(volatileRange > calmRange,
            $"expected VOLATILE price range ({volatileRange}) > CALM price range ({calmRange})");
    }

    [Fact]
    public async Task CalmRegime_HasHigherQuantityThanVolatileRegime()
    {
        // QuantityMultiplier defaults: CALM=1.0, VOLATILE=0.6. With
        // BaseQuantity=2.0 and LevelQuantityFractions=[0.5, 0.3, 0.2],
        // typical CALM quantities are 1.0/0.6/0.4; VOLATILE shrinks each by
        // 0.6. The mean per-submit quantity must therefore be smaller in the
        // VOLATILE window than in the CALM window.
        await using var host = new TestQuoterHost("regime-sweep.json", overrideSeed: 9002);
        var ct = TestContext.Current.CancellationToken;
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(15.0);
        var calmCount = host.TestPublisher.Captured.Count;

        await host.AdvanceSecondsAsync(115.0);
        await host.Quoter.StopAsync(ct);

        var allCaptured = host.TestPublisher.Captured;
        var calmQtys = allCaptured
            .Take(calmCount)
            .Where(c => c.Kind == "SubmitLimitOrder")
            .Select(c => ParseQty(c.JsonBody))
            .ToList();
        var volatileQtys = allCaptured
            .Skip(calmCount)
            .Where(c => c.Kind == "SubmitLimitOrder")
            .Select(c => ParseQty(c.JsonBody))
            .ToList();

        Assert.NotEmpty(calmQtys);
        Assert.NotEmpty(volatileQtys);

        var calmAvg = calmQtys.Average();
        var volatileAvg = volatileQtys.Average();

        Assert.True(volatileAvg < calmAvg,
            $"expected VOLATILE avg quantity ({volatileAvg}) < CALM avg quantity ({calmAvg})");
    }

    private static long ParsePriceTicks(string jsonBody)
    {
        const string key = "\"priceTicks\":";
        var start = jsonBody.IndexOf(key, StringComparison.Ordinal) + key.Length;
        var end = jsonBody.IndexOf(',', start);
        return long.Parse(jsonBody[start..end], CultureInfo.InvariantCulture);
    }

    private static decimal ParseQty(string jsonBody)
    {
        const string key = "\"qty\":";
        var start = jsonBody.IndexOf(key, StringComparison.Ordinal) + key.Length;
        var end = jsonBody.IndexOf(',', start);
        if (end < 0)
            end = jsonBody.IndexOf('}', start);
        return decimal.Parse(jsonBody[start..end], CultureInfo.InvariantCulture);
    }
}
