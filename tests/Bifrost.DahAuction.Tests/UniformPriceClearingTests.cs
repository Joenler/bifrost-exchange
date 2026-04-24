using System.Security.Cryptography;
using System.Text;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction.Clearing;
using Xunit;

namespace Bifrost.DahAuction.Tests;

/// <summary>
/// Unit tests for <see cref="UniformPriceClearing.Compute"/>. Covers hand-worked
/// vector, determinism, pro-rata at margin, no-cross (both sides and single
/// side), negative-price clearing, and a pedagogical-documentation grep that
/// asserts the source file carries the prose explainer + EUPHEMIA citation.
/// </summary>
public sealed class UniformPriceClearingTests
{
    private const string Q2 = "DE.Quarter.9999-01-01T00:15";

    [Fact]
    public void HandWorked_4Team_ExactCrossingAt85()
    {
        // Hand-worked vector (from the uniform-price clearing spec):
        //   alpha buy:  [(100, 30), (80, 20)]
        //   beta  buy:  [(90, 40)]
        //   gamma sell: [(70, 50), (95, 30)]
        //   delta sell: [(85, 20)]
        //
        // Aggregate demand (desc): 30@100, 40@90, 20@80
        // Aggregate supply (asc):  50@70, 20@85, 30@95
        //
        // At p*=85: supply = 70 (50@70 + 20@85), demand = 70 (30@100 + 40@90).
        // Exact crossing; clearing price = 85.
        // Awards:
        //   alpha: +30 (buy at p*=85)
        //   beta:  +40
        //   gamma: -50 (sell at p*=85)
        //   delta: -20

        var alpha = new BidMatrixDto("alpha", Q2,
            new BidStepDto[] { new(100L, 30L), new(80L, 20L) },
            Array.Empty<BidStepDto>());
        var beta = new BidMatrixDto("beta", Q2,
            new BidStepDto[] { new(90L, 40L) },
            Array.Empty<BidStepDto>());
        var gamma = new BidMatrixDto("gamma", Q2,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(70L, 50L), new(95L, 30L) });
        var delta = new BidMatrixDto("delta", Q2,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(85L, 20L) });

        var outcome = UniformPriceClearing.Compute(Q2, new[] { alpha, beta, gamma, delta });

        Assert.True(outcome.DidCross);
        Assert.Equal(85L, outcome.ClearingPriceTicks);

        var awards = outcome.Awards.ToDictionary(x => x.TeamName, x => x.AwardedQuantityTicks);
        Assert.Equal(30L, awards["alpha"]);
        Assert.Equal(40L, awards["beta"]);
        Assert.Equal(-50L, awards["gamma"]);
        Assert.Equal(-20L, awards["delta"]);
    }

    [Fact]
    public void Determinism_ByteIdentical_SHA256()
    {
        // Run Compute twice on identical inputs; SHA-256 of serialized awards
        // must match. Closes the determinism acceptance for the clearing engine.
        var bids = new[]
        {
            new BidMatrixDto("alpha", Q2,
                new BidStepDto[] { new(100L, 30L), new(80L, 20L) },
                Array.Empty<BidStepDto>()),
            new BidMatrixDto("gamma", Q2,
                Array.Empty<BidStepDto>(),
                new BidStepDto[] { new(70L, 40L) }),
        };

        var o1 = UniformPriceClearing.Compute(Q2, bids);
        var o2 = UniformPriceClearing.Compute(Q2, bids);

        Assert.Equal(Hash(o1), Hash(o2));
    }

    private static string Hash(ClearingOutcome o)
    {
        var sb = new StringBuilder();
        sb.Append(o.QuarterId).Append('|').Append(o.DidCross).Append('|').Append(o.ClearingPriceTicks);
        foreach (var (team, qty) in o.Awards)
        {
            sb.Append('|').Append(team).Append(':').Append(qty);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    [Fact]
    public void NoCross_AllBuysBelowAllSells()
    {
        var alpha = new BidMatrixDto("alpha", Q2,
            new BidStepDto[] { new(50L, 10L) },
            Array.Empty<BidStepDto>());
        var gamma = new BidMatrixDto("gamma", Q2,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(100L, 10L) });
        var outcome = UniformPriceClearing.Compute(Q2, new[] { alpha, gamma });
        Assert.False(outcome.DidCross);
        Assert.Equal(0L, outcome.ClearingPriceTicks);
        Assert.Empty(outcome.Awards);
    }

    [Fact]
    public void NoCross_OnlyBuys()
    {
        var alpha = new BidMatrixDto("alpha", Q2,
            new BidStepDto[] { new(100L, 10L) },
            Array.Empty<BidStepDto>());
        var outcome = UniformPriceClearing.Compute(Q2, new[] { alpha });
        Assert.False(outcome.DidCross);
    }

    [Fact]
    public void NoCross_OnlySells()
    {
        var gamma = new BidMatrixDto("gamma", Q2,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(70L, 10L) });
        var outcome = UniformPriceClearing.Compute(Q2, new[] { gamma });
        Assert.False(outcome.DidCross);
    }

    [Fact]
    public void NegativePrice_Clearing_AwardsExpectedDirection()
    {
        // Renewable-surplus hour: generator willing to pay buyers to absorb
        // power. Buy step at 0, sell step at -100; they cross between -100
        // and 0. FindCrossingPrice returns the LOWEST price where S(p) >= D(p)
        // with positive volume on both sides, so p* = -100.
        var alpha = new BidMatrixDto("alpha", Q2,
            new BidStepDto[] { new(0L, 10L) },
            Array.Empty<BidStepDto>());
        var gamma = new BidMatrixDto("gamma", Q2,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(-100L, 10L) });

        var outcome = UniformPriceClearing.Compute(Q2, new[] { alpha, gamma });
        Assert.True(outcome.DidCross);
        Assert.Equal(-100L, outcome.ClearingPriceTicks);
        var awards = outcome.Awards.ToDictionary(x => x.TeamName, x => x.AwardedQuantityTicks);
        Assert.Equal(10L, awards["alpha"]);
        Assert.Equal(-10L, awards["gamma"]);
    }

    [Fact]
    public void Prorata_Partial_Fill_AtMarginalPrice()
    {
        // Two teams share the marginal sell price; short side is demand.
        //   Demand: 25 at 100 (alpha).
        //   Supply: 10 at 50 (gamma) + 12 at 80 (gamma) + 8 at 80 (delta).
        //
        // Cumulative demand at p=80: 25 (alpha@100 still in the money).
        // Cumulative supply at p=80: 30 (10@50 + 12@80 + 8@80).
        // Crossing at p*=80 with matchedVolume=25. Short side = buy (25
        // total). Long side = sell: 10@50 fills fully (10), margin tier
        // 20@80 must partial-fill for remaining 15.
        //   beta(12) + delta(8) at marginal price 80; total = 20; residual = 15.
        //     gamma(@80 step index 1 within gamma): floor(15 * 12 / 20) = 9
        //     delta(@80 step index 0 within delta): floor(15 * 8 / 20) = 6
        //   Total allocated: 15 — exact; no remainder distribution.
        //
        // Awards:
        //   alpha: +25 (buy at p*=80)
        //   gamma: -(10 + 9) = -19 (sell 10 infra-marginal + 9 prorata)
        //   delta: -6 (sell 6 prorata)

        var alpha = new BidMatrixDto("alpha", Q2,
            new BidStepDto[] { new(100L, 25L) },
            Array.Empty<BidStepDto>());
        var gamma = new BidMatrixDto("gamma", Q2,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(50L, 10L), new(80L, 12L) });
        var delta = new BidMatrixDto("delta", Q2,
            Array.Empty<BidStepDto>(),
            new BidStepDto[] { new(80L, 8L) });

        var outcome = UniformPriceClearing.Compute(Q2, new[] { alpha, gamma, delta });
        Assert.True(outcome.DidCross);
        Assert.Equal(80L, outcome.ClearingPriceTicks);
        var awards = outcome.Awards.ToDictionary(x => x.TeamName, x => x.AwardedQuantityTicks);
        Assert.Equal(25L, awards["alpha"]);
        Assert.Equal(-19L, awards["gamma"]);
        Assert.Equal(-6L, awards["delta"]);
        // Buy total 25 = sell total 19 + 6 — closure check.
    }

    [Fact]
    public void PedagogicalArtifacts_Present_InSourceFile()
    {
        // Pedagogical-documentation presence check: file-level pedagogical artifacts.
        // This test greps the source file directly to avoid drift between
        // the source and the "we remembered to document it" assertion.
        var sourcePath = FindSourceFile("UniformPriceClearing.cs");
        var text = File.ReadAllText(sourcePath);

        Assert.Contains("EUPHEMIA", text);
        Assert.Contains("nemo-committee.eu", text);
        Assert.Contains("<summary>", text);  // XML doc on Compute
        Assert.Contains("// Stage 1", text);
        Assert.Contains("// Stage 2", text);
        Assert.Contains("// Stage 3", text);
        Assert.Contains("// Stage 4", text);
        Assert.Contains("// Stage 5", text);

        // Prose header >= 10 lines of // comments above the namespace.
        var lines = text.Split('\n');
        int leadingComments = 0;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("//"))
            {
                leadingComments++;
            }
            else if (t.StartsWith("namespace") || t.StartsWith("using"))
            {
                break;
            }
        }
        Assert.True(leadingComments >= 10,
            $"Expected >= 10 lines of prose header comments before namespace/using; found {leadingComments}.");
    }

    private static string FindSourceFile(string fileName)
    {
        // Walk up from test-bin output to the solution root, then locate the source.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "src", "dah-auction", "Clearing", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException($"Could not locate {fileName} walking up from {AppContext.BaseDirectory}");
    }
}
