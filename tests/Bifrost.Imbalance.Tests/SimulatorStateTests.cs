using Bifrost.Exchange.Application.RoundState;
using Xunit;

namespace Bifrost.Imbalance.Tests;

public class SimulatorStateTests
{
    [Fact]
    public void InitialState_EmptyMaps_ZeroPhysicals_DefaultMetadata()
    {
        var state = new SimulatorState();

        Assert.Empty(state.NetPositions);
        Assert.Equal(new long[] { 0, 0, 0, 0 }, state.APhysicalQh);
        Assert.Empty(state.PendingTransients);
        Assert.Equal(RoundState.IterationOpen, state.CurrentRoundState);
        Assert.Equal(0, state.CurrentRoundNumber);
        Assert.Equal("Calm", state.CurrentRegime);
    }

    [Fact]
    public void ResetForNewRound_ClearsAccumulatorsPreservesMetadata()
    {
        var state = new SimulatorState();
        state.NetPositions[("alpha", 2)] = 16_000L;
        state.NetPositions[("bravo", 1)] = -4_000L;
        state.APhysicalQh[2] = -30_000L;
        state.APhysicalQh[0] = 5_000L;
        state.PendingTransients.Add(new TransientShock(0L, 30_000_000_000L, 1, -5_000L));
        state.CurrentRoundNumber = 5;
        state.CurrentRegime = "Volatile";

        state.ResetForNewRound();

        Assert.Empty(state.NetPositions);
        Assert.Equal(new long[] { 0, 0, 0, 0 }, state.APhysicalQh);
        Assert.Empty(state.PendingTransients);

        // Metadata is explicitly NOT reset — the drain loop updates those from the
        // incoming RoundStateMessage. ResetForNewRound only clears accumulators.
        Assert.Equal(5, state.CurrentRoundNumber);
        Assert.Equal("Volatile", state.CurrentRegime);
    }

    [Fact]
    public void NetPositions_TupleKey_IsolatesByClientAndQuarter()
    {
        var state = new SimulatorState();
        state.NetPositions[("alpha", 0)] = 100L;
        state.NetPositions[("alpha", 2)] = -50L;
        state.NetPositions[("bravo", 2)] = 200L;

        Assert.Equal(100L, state.NetPositions[("alpha", 0)]);
        Assert.Equal(-50L, state.NetPositions[("alpha", 2)]);
        Assert.Equal(200L, state.NetPositions[("bravo", 2)]);
        Assert.False(state.NetPositions.ContainsKey(("alpha", 1)));
        Assert.False(state.NetPositions.ContainsKey(("charlie", 2)));
    }

    [Fact]
    public void APhysicalQh_HasExactlyFourSlots()
    {
        var state = new SimulatorState();
        Assert.Equal(4, state.APhysicalQh.Length);
    }

    [Fact]
    public void PendingTransients_RecordCarriesWindowAndContribution()
    {
        var state = new SimulatorState();
        var shock = new TransientShock(
            ActivatedTsNs: 1_000_000_000L,
            TransientWindowNs: 30_000_000_000L,
            QuarterIndex: 2,
            ContributionTicks: -15_000L);

        state.PendingTransients.Add(shock);

        var stored = Assert.Single(state.PendingTransients);
        Assert.Equal(1_000_000_000L, stored.ActivatedTsNs);
        Assert.Equal(30_000_000_000L, stored.TransientWindowNs);
        Assert.Equal(2, stored.QuarterIndex);
        Assert.Equal(-15_000L, stored.ContributionTicks);
    }

    [Fact]
    public void CurrentRoundState_Settable_ReflectsLatestAssignment()
    {
        var state = new SimulatorState();
        state.CurrentRoundState = RoundState.RoundOpen;
        Assert.Equal(RoundState.RoundOpen, state.CurrentRoundState);

        state.CurrentRoundState = RoundState.Gate;
        Assert.Equal(RoundState.Gate, state.CurrentRoundState);
    }
}
