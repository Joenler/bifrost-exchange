using Bifrost.Exchange.Application.RoundState;
using Bifrost.Quoter.Tests.Fixtures;
using Xunit;

namespace Bifrost.Quoter.Tests.Integration;

/// <summary>
/// QTR-06 integration tests. The Quoter must quote only when
/// <c>IRoundStateSource.Current == RoundOpen</c>. Transition into Gate (or
/// any non-RoundOpen state) must stop new commands; transition back into
/// RoundOpen must resume quoting.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class RoundStateReactivityTests
{
    [Fact]
    public async Task RoundOpen_EnablesQuoting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost(
            "calm-drift.json",
            overrideSeed: 1,
            initialState: RoundState.RoundOpen);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(10.0);
        await host.Quoter.StopAsync(ct);

        Assert.NotEmpty(host.TestPublisher.Captured);
    }

    [Fact]
    public async Task Gate_StopsQuoting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost(
            "calm-drift.json",
            overrideSeed: 1,
            initialState: RoundState.RoundOpen);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(5.0);

        var openCount = host.TestPublisher.Captured.Count;
        Assert.True(openCount > 0, "expected quoter to have produced commands during RoundOpen");

        // Flip to Gate -- the quoter's tick-loop gate should short-circuit
        // before issuing any new commands.
        host.RoundStateSource.Transition(RoundState.Gate);
        await host.AdvanceSecondsAsync(10.0);
        await host.Quoter.StopAsync(ct);

        var afterGateCount = host.TestPublisher.Captured.Count;
        Assert.Equal(openCount, afterGateCount);
    }

    [Fact]
    public async Task RoundOpen_Resume()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost(
            "calm-drift.json",
            overrideSeed: 1,
            initialState: RoundState.RoundOpen);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(5.0);

        host.RoundStateSource.Transition(RoundState.Gate);
        await host.AdvanceSecondsAsync(5.0);
        var midCount = host.TestPublisher.Captured.Count;

        host.RoundStateSource.Transition(RoundState.RoundOpen);
        await host.AdvanceSecondsAsync(10.0);
        await host.Quoter.StopAsync(ct);

        var resumedCount = host.TestPublisher.Captured.Count;
        Assert.True(resumedCount > midCount,
            $"expected new commands after RoundOpen resume; before={midCount} after={resumedCount}");
    }

    [Theory]
    [InlineData(RoundState.IterationOpen)]
    [InlineData(RoundState.AuctionOpen)]
    [InlineData(RoundState.AuctionClosed)]
    [InlineData(RoundState.Gate)]
    [InlineData(RoundState.Settled)]
    [InlineData(RoundState.Aborted)]
    public async Task NonRoundOpen_States_DoNotQuote(RoundState nonRoundOpenState)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost(
            "calm-drift.json",
            overrideSeed: 1,
            initialState: nonRoundOpenState);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(10.0);
        await host.Quoter.StopAsync(ct);

        // No order commands should be emitted; RegimeChange envelopes are
        // also suppressed because the gate exits before schedule.Advance.
        var orderCount = host.TestPublisher.Captured.Count(c =>
            c.Kind == "SubmitLimitOrder" || c.Kind == "CancelOrder" || c.Kind == "ReplaceOrder");
        Assert.Equal(0, orderCount);
    }
}
