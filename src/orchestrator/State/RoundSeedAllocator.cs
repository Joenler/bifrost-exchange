namespace Bifrost.Orchestrator.State;

/// <summary>
/// Deterministic scenario-seed allocator. SplitMix64 hash over
/// <c>masterSeed XOR roundNumber</c> for scored-round seeds; over
/// <c>masterSeed XOR (-1L - rotationCount)</c> for iteration-window seeds.
/// The negative-offset XOR mix guarantees collision-free separation between
/// the two sequences for every <c>(roundNumber, rotationCount)</c> pair under
/// realistic event horizons (≤ 6 scored rounds, ≤ 50 iteration rotations).
/// </summary>
/// <remarks>
/// Replay guarantee: same <paramref name="masterSeed"/> + same
/// <c>roundNumber</c> always yields the same seed. Tests in
/// <c>ScenarioSeedDeterminismTests</c> cover byte-identical seed sequences
/// across two allocator instances and disjoint scored/iteration spaces.
///
/// SplitMix64 is the well-known high-quality PRNG used by Java 8's
/// <c>SplittableRandom</c>: ~10-bit avalanche from a 64-bit input state to a
/// uniformly distributed 64-bit output. Reference:
/// <c>https://prng.di.unimi.it/splitmix64.c</c>.
///
/// No <c>Random.Shared</c>, no <c>System.Random</c> — pure bit-twiddling
/// satisfies the <c>build/BannedSymbols.txt</c> fence and keeps the seed
/// path side-effect free (no constructed RNG state to thread through).
/// </remarks>
public sealed class RoundSeedAllocator
{
    private readonly long _masterSeed;

    public RoundSeedAllocator(long masterSeed)
    {
        _masterSeed = masterSeed;
    }

    /// <summary>
    /// Master seed surfaced for diagnostics + replay tooling. Persisted in
    /// <c>OrchestratorState.MasterSeed</c> on disk.
    /// </summary>
    public long MasterSeed => _masterSeed;

    /// <summary>
    /// Deterministic scored-round seed. Called by the orchestrator actor on
    /// every <c>AuctionOpen</c> transition; the result is written to
    /// <c>OrchestratorState.ScenarioSeedInternal</c> and stays off the wire
    /// (D-21 hide-on-wire rule — wire <c>scenario_seed=0</c> during scored
    /// rounds).
    /// </summary>
    public long NextScoredRoundSeed(int roundNumber) =>
        SplitMix64((ulong)_masterSeed ^ (ulong)(long)roundNumber);

    /// <summary>
    /// Deterministic iteration-window seed. Called on every
    /// <c>IterationSeedTickMessage</c> with the incremented rotation count.
    /// The negative-offset (<c>-1L - rotationCount</c>) XOR mix keeps the
    /// iteration-seed space disjoint from the scored-round-seed space across
    /// every realistic <c>(roundNumber, rotationCount)</c> pair.
    /// Exposed on the wire during <c>IterationOpen</c>.
    /// </summary>
    public long CurrentIterationSeed(int rotationCount) =>
        SplitMix64((ulong)_masterSeed ^ (ulong)(-1L - rotationCount));

    /// <summary>
    /// SplitMix64 — well-known high-quality 64-bit hash. Three multiply +
    /// xor-shift rounds with the canonical magic constants (2^64 / golden
    /// ratio + two avalanche multipliers from Stafford's mix13).
    /// </summary>
    private static long SplitMix64(ulong x)
    {
        ulong z = x + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return unchecked((long)(z ^ (z >> 31)));
    }
}
