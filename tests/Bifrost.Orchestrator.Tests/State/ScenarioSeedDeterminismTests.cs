using Bifrost.Orchestrator.State;
using Xunit;

namespace Bifrost.Orchestrator.Tests.State;

/// <summary>
/// Determinism gate for <see cref="RoundSeedAllocator"/>: same masterSeed +
/// same roundNumber sequence produces byte-identical scored-round seeds across
/// runs. Different masterSeeds produce different sequences. Iteration-seed
/// space is disjoint from scored-round-seed space (negative-offset XOR mix).
/// Pure-function: repeated calls return the same value.
/// </summary>
/// <remarks>
/// Closes ORC-05 acceptance criterion: "Two runs with master_seed=42 and the
/// same 3-round scripted sequence produce byte-identical scenario_seed_internal
/// at each AuctionOpen". The negative-offset (-1L - rotationCount) iteration
/// space is checked across a 50x50 product to prove no collision exists in
/// the realistic event horizon (≤ 6 scored rounds, ≤ 50 rotations per
/// IterationOpen window).
/// </remarks>
public sealed class ScenarioSeedDeterminismTests
{
    [Fact]
    public void SameMasterSeed_SameRoundNumbers_ProducesByteIdenticalSeeds()
    {
        RoundSeedAllocator a = new(masterSeed: 42);
        RoundSeedAllocator b = new(masterSeed: 42);

        long[] aSeeds =
        {
            a.NextScoredRoundSeed(1),
            a.NextScoredRoundSeed(2),
            a.NextScoredRoundSeed(3),
        };
        long[] bSeeds =
        {
            b.NextScoredRoundSeed(1),
            b.NextScoredRoundSeed(2),
            b.NextScoredRoundSeed(3),
        };

        Assert.Equal(aSeeds, bSeeds);
    }

    [Fact]
    public void DifferentMasterSeed_ProducesDifferentSeed_ForSameRoundNumber()
    {
        RoundSeedAllocator a = new(masterSeed: 42);
        RoundSeedAllocator b = new(masterSeed: 43);

        Assert.NotEqual(a.NextScoredRoundSeed(1), b.NextScoredRoundSeed(1));
    }

    [Fact]
    public void ScoredRoundAndIterationSeedSpaces_AreDisjoint_OverRealisticHorizon()
    {
        RoundSeedAllocator a = new(masterSeed: 42);

        HashSet<long> scoredSeeds = Enumerable.Range(1, 50)
            .Select(i => a.NextScoredRoundSeed(i))
            .ToHashSet();
        HashSet<long> iterationSeeds = Enumerable.Range(0, 50)
            .Select(i => a.CurrentIterationSeed(i))
            .ToHashSet();

        int overlap = scoredSeeds.Intersect(iterationSeeds).Count();
        Assert.Equal(0, overlap);
    }

    [Fact]
    public void NextScoredRoundSeed_IsPureFunction_RepeatedCallsReturnSameValue()
    {
        RoundSeedAllocator a = new(masterSeed: 99);

        long first = a.NextScoredRoundSeed(7);
        long second = a.NextScoredRoundSeed(7);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CurrentIterationSeed_IsPureFunction_RepeatedCallsReturnSameValue()
    {
        RoundSeedAllocator a = new(masterSeed: 99);

        long first = a.CurrentIterationSeed(3);
        long second = a.CurrentIterationSeed(3);

        Assert.Equal(first, second);
    }

    [Fact]
    public void NextScoredRoundSeed_NonZero_ForNonZeroRoundNumber()
    {
        // Stub returned 0L; the real SplitMix64 impl produces a non-zero
        // value for any non-zero input under masterSeed=42. Guards against
        // a regression to the stub.
        RoundSeedAllocator a = new(masterSeed: 42);

        Assert.NotEqual(0L, a.NextScoredRoundSeed(1));
    }
}
