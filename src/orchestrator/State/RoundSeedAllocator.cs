namespace Bifrost.Orchestrator.State;

/// <summary>
/// Stub shell for the actor-loop wiring plan. A follow-up plan fills in the
/// SplitMix64 math. Constructor + public method surface is LOCKED here — the
/// follow-up plan MUST NOT widen or change any public signature.
/// </summary>
public sealed class RoundSeedAllocator
{
    private readonly long _masterSeed;

    public RoundSeedAllocator(long masterSeed)
    {
        _masterSeed = masterSeed;
    }

    /// <summary>
    /// Master seed surfaced for future use (SplitMix64 will mix with
    /// round_number). Exposed via method so the field is observed at call
    /// sites — keeps the stub honest under nullable/unused-field analysers.
    /// </summary>
    public long MasterSeed => _masterSeed;

    /// <summary>Returns 0L in this plan (stub). A follow-up plan fills in real math.</summary>
    public long NextScoredRoundSeed(int roundNumber) => 0L;

    /// <summary>Returns 0L in this plan (stub). A follow-up plan fills in real math.</summary>
    public long CurrentIterationSeed(int rotationCount) => 0L;
}
