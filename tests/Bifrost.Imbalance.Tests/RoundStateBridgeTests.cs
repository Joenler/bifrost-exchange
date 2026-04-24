using System.Threading.Channels;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.HostedServices;
using Bifrost.Imbalance.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// Unit tests for <see cref="RoundStateBridgeHostedService"/> — the adapter
/// that forwards <see cref="IRoundStateSource.OnChange"/> raises onto the
/// simulator's shared <see cref="Channel{T}"/> of <see cref="SimulatorMessage"/>.
/// <para>
/// The bridge is thin by design; the invariants worth locking are:
/// </para>
/// <list type="bullet">
///   <item>Every <see cref="IRoundStateSource.OnChange"/> raise produces exactly
///   one <see cref="RoundStateMessage"/> on the channel — the full 7-state cycle
///   emits exactly 6 messages (initial state is already set; 6 transitions follow).</item>
///   <item><see cref="IHostedService.StopAsync"/> detaches the handler so further
///   raises do not enqueue — leaked handlers past shutdown are an availability
///   hazard (strong reference into a completed channel).</item>
///   <item>No-op transitions (Set the same state) emit nothing. The mock source
///   short-circuits same-state calls and never raises, so zero messages land.</item>
/// </list>
/// <para>
/// These tests construct the channel + bridge directly (not via
/// <see cref="TestImbalanceHost"/>) so the drain loop does not consume messages
/// mid-assert. The test reads the channel after completing it.
/// </para>
/// </summary>
public class RoundStateBridgeTests
{
    [Fact]
    public async Task SevenStateCycle_EmitsExactlySixRoundStateMessages()
    {
        // Arrange
        var clock = new FakeClock();
        var source = new MockRoundStateSource(clock, RoundState.IterationOpen);
        var channel = Channel.CreateBounded<SimulatorMessage>(
            new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        var bridge = new RoundStateBridgeHostedService(
            source,
            channel,
            NullLogger<RoundStateBridgeHostedService>.Instance);
        await bridge.StartAsync(TestContext.Current.CancellationToken);

        // Drive the full 7-state cycle. MockRoundStateSource.Set skips same-state
        // transitions, so starting at IterationOpen and setting IterationOpen
        // again emits nothing. The sequence below produces 6 raises
        // (IterationOpen -> AuctionOpen -> AuctionClosed -> RoundOpen -> Gate ->
        // Settled -> IterationOpen).
        var sequence = new[]
        {
            RoundState.AuctionOpen,
            RoundState.AuctionClosed,
            RoundState.RoundOpen,
            RoundState.Gate,
            RoundState.Settled,
            RoundState.IterationOpen,
        };
        foreach (var s in sequence)
        {
            source.Set(s);
        }

        // Complete the channel and drain — OnChange is synchronous so every
        // TryWrite has completed by the time Set returns.
        channel.Writer.TryComplete();

        var received = new List<SimulatorMessage>();
        await foreach (var m in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            received.Add(m);
        }

        // Assert
        Assert.Equal(sequence.Length, received.Count);
        Assert.All(received, m => Assert.IsType<RoundStateMessage>(m));

        // First transition carries the previous-state correctly.
        var first = (RoundStateMessage)received[0];
        Assert.Equal(RoundState.IterationOpen, first.Previous);
        Assert.Equal(RoundState.AuctionOpen, first.Current);

        // Last transition wraps back to IterationOpen via Settled.
        var last = (RoundStateMessage)received[^1];
        Assert.Equal(RoundState.Settled, last.Previous);
        Assert.Equal(RoundState.IterationOpen, last.Current);

        // Full transition ordering is preserved — the drain loop depends on this
        // so the round-state gate flips in the correct order on replay.
        var expectedPairs = new[]
        {
            (RoundState.IterationOpen, RoundState.AuctionOpen),
            (RoundState.AuctionOpen, RoundState.AuctionClosed),
            (RoundState.AuctionClosed, RoundState.RoundOpen),
            (RoundState.RoundOpen, RoundState.Gate),
            (RoundState.Gate, RoundState.Settled),
            (RoundState.Settled, RoundState.IterationOpen),
        };
        for (var i = 0; i < expectedPairs.Length; i++)
        {
            var m = (RoundStateMessage)received[i];
            Assert.Equal(expectedPairs[i].Item1, m.Previous);
            Assert.Equal(expectedPairs[i].Item2, m.Current);
        }

        await bridge.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_DetachesHandler_FurtherEventsNotForwarded()
    {
        // Arrange
        var clock = new FakeClock();
        var source = new MockRoundStateSource(clock, RoundState.IterationOpen);
        var channel = Channel.CreateBounded<SimulatorMessage>(
            new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        var bridge = new RoundStateBridgeHostedService(
            source,
            channel,
            NullLogger<RoundStateBridgeHostedService>.Instance);
        await bridge.StartAsync(TestContext.Current.CancellationToken);

        // Pre-stop: one transition lands.
        source.Set(RoundState.AuctionOpen);
        Assert.True(channel.Reader.TryRead(out var first));
        Assert.IsType<RoundStateMessage>(first);

        // Act — stop the bridge.
        await bridge.StopAsync(TestContext.Current.CancellationToken);

        // Post-stop: further transitions must NOT enqueue. The handler is
        // detached in StopAsync BEFORE base.StopAsync runs so a transition
        // raised on the raising thread after this point does not reach the
        // channel.
        source.Set(RoundState.AuctionClosed);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NoopSameStateTransition_EmitsZeroMessages()
    {
        // Arrange — source starts in RoundOpen; Set(RoundOpen) must not raise.
        var clock = new FakeClock();
        var source = new MockRoundStateSource(clock, RoundState.RoundOpen);
        var channel = Channel.CreateBounded<SimulatorMessage>(
            new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        var bridge = new RoundStateBridgeHostedService(
            source,
            channel,
            NullLogger<RoundStateBridgeHostedService>.Instance);
        await bridge.StartAsync(TestContext.Current.CancellationToken);

        // Act — set the SAME state; MockRoundStateSource short-circuits no-op
        // transitions and never raises OnChange. The bridge has nothing to
        // forward.
        source.Set(RoundState.RoundOpen);

        // Assert — zero messages on the channel.
        Assert.False(channel.Reader.TryRead(out _));

        await bridge.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ctor_AttachesHandler_BeforeStartAsync()
    {
        // The ctor attaches the handler so transitions raised between
        // service construction (DI container build) and IHostedService.StartAsync
        // are not missed. This is a load-bearing invariant — the MC console
        // could in principle raise a transition on the source during the host's
        // startup window.
        var clock = new FakeClock();
        var source = new MockRoundStateSource(clock, RoundState.IterationOpen);
        var channel = Channel.CreateBounded<SimulatorMessage>(
            new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        var bridge = new RoundStateBridgeHostedService(
            source,
            channel,
            NullLogger<RoundStateBridgeHostedService>.Instance);

        // Note: StartAsync has NOT been called yet.
        source.Set(RoundState.AuctionOpen);

        Assert.True(channel.Reader.TryRead(out var msg));
        var rs = Assert.IsType<RoundStateMessage>(msg);
        Assert.Equal(RoundState.IterationOpen, rs.Previous);
        Assert.Equal(RoundState.AuctionOpen, rs.Current);

        // Clean up — StartAsync + StopAsync so the handler is correctly released.
        await bridge.StartAsync(TestContext.Current.CancellationToken);
        await bridge.StopAsync(TestContext.Current.CancellationToken);
    }
}
