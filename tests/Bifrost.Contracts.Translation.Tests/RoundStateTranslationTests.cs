using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Round;
using Google.Protobuf;
using Xunit;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row: bifrost.round.v1.RoundState ↔
/// Bifrost.Contracts.Internal.Events.RoundStateChangedPayload.
///
/// The proto carries 5 fields (state, round_number, scenario_seed,
/// transition_ns, expected_next_transition_ns); the DTO carries 12 fields,
/// the same 5 plus 7 orchestrator-internal fields (Paused, PausedReason,
/// Blocked, BlockedReason, IsReconciliation, IterationSeedRotationCount,
/// AbortReason) that are reconstructed on the consumer side from envelope
/// headers + orchestrator state. The 7 orchestrator-internal fields do NOT
/// participate in the proto ↔ DTO byte-equivalence check — only the 5
/// shared fields round-trip bit-equivalently.
/// </summary>
public sealed class RoundStateTranslationTests
{
    [Fact]
    public void Roundtrip_ByteEquivalent_ScoredRound_HiddenSeed()
    {
        // Scored-round publish: ScenarioSeed is 0 on the wire (hide-on-wire rule).
        var original = new RoundState
        {
            State = State.RoundOpen,
            RoundNumber = 3,
            ScenarioSeed = 0L,
            TransitionNs = 1_714_516_800_000_000_123L,
            ExpectedNextTransitionNs = 1_714_516_800_000_000_999L,
        };
        var originalBytes = original.ToByteArray();

        var dto = new RoundStateChangedPayload(
            State: original.State.ToString(),
            RoundNumber: original.RoundNumber,
            ScenarioSeedOnWire: original.ScenarioSeed,
            TransitionNs: original.TransitionNs,
            ExpectedNextTransitionNs: original.ExpectedNextTransitionNs,
            Paused: false,
            PausedReason: null,
            Blocked: false,
            BlockedReason: null,
            IsReconciliation: false,
            IterationSeedRotationCount: 0,
            AbortReason: null);

        var roundtrip = new RoundState
        {
            State = Enum.Parse<State>(dto.State),
            RoundNumber = dto.RoundNumber,
            ScenarioSeed = dto.ScenarioSeedOnWire,
            TransitionNs = dto.TransitionNs,
            ExpectedNextTransitionNs = dto.ExpectedNextTransitionNs.GetValueOrDefault(),
        };
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }

    [Fact]
    public void Roundtrip_ByteEquivalent_IterationOpen_RollingSeed()
    {
        // IterationOpen publish: ScenarioSeed is exposed on the wire.
        var original = new RoundState
        {
            State = State.IterationOpen,
            RoundNumber = 0,
            ScenarioSeed = -4_611_686_018_427_388_712L,
            TransitionNs = 1_714_516_800_000_000_500L,
            ExpectedNextTransitionNs = 0L,
        };
        var originalBytes = original.ToByteArray();

        var dto = new RoundStateChangedPayload(
            State: original.State.ToString(),
            RoundNumber: original.RoundNumber,
            ScenarioSeedOnWire: original.ScenarioSeed,
            TransitionNs: original.TransitionNs,
            ExpectedNextTransitionNs: null,
            Paused: false,
            PausedReason: null,
            Blocked: false,
            BlockedReason: null,
            IsReconciliation: false,
            IterationSeedRotationCount: 7,
            AbortReason: null);

        var roundtrip = new RoundState
        {
            State = Enum.Parse<State>(dto.State),
            RoundNumber = dto.RoundNumber,
            ScenarioSeed = dto.ScenarioSeedOnWire,
            TransitionNs = dto.TransitionNs,
            ExpectedNextTransitionNs = dto.ExpectedNextTransitionNs.GetValueOrDefault(),
        };
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
