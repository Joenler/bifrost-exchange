using Bifrost.Contracts.Internal;
using Bifrost.Gateway.State;
using Bifrost.Time;
using Xunit;

namespace Bifrost.Gateway.Tests.State;

public class TeamRegistryTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset GetUtcNow() => now;
    }

    private static TeamRegistry NewRegistry() =>
        new(new FixedClock(new DateTimeOffset(2026, 4, 24, 0, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData("quoter")]
    [InlineData("QUOTER")]
    [InlineData("Quoter")]
    [InlineData("dah-auction")]
    [InlineData("Dah-Auction")]
    [InlineData("DAH-AUCTION")]
    public void TryRegister_ReservedTeamName_Rejected(string reserved)
    {
        var reg = NewRegistry();
        var result = reg.TryRegister(reserved, 0);
        Assert.False(result.Success);
        Assert.Null(result.TeamState);
        Assert.NotNull(result.FailureDetail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRegister_EmptyTeamName_Rejected(string empty)
    {
        var reg = NewRegistry();
        var result = reg.TryRegister(empty, 0);
        Assert.False(result.Success);
    }

    [Fact]
    public void TryRegister_FreshTeam_AssignsStableClientId()
    {
        var reg = NewRegistry();
        var first = reg.TryRegister("alpha", 0);
        Assert.True(first.Success);
        Assert.NotNull(first.TeamState);
        Assert.Equal(0, first.ResumedFromSequence);
        Assert.False(first.ReregisterRequired);
        Assert.NotEmpty(first.TeamState!.ClientId);
    }

    [Fact]
    public void TryRegister_Reconnect_ReturnsSameClientId()
    {
        var reg = NewRegistry();
        var first = reg.TryRegister("alpha", 0);
        var second = reg.TryRegister("alpha", 0);
        Assert.True(second.Success);
        Assert.Equal(first.TeamState!.ClientId, second.TeamState!.ClientId);
    }

    [Fact]
    public void TryRegister_ResumeWithinRetention_NotReregister()
    {
        var reg = NewRegistry();
        var first = reg.TryRegister("alpha", 0);
        var team = first.TeamState!;
        // Append a few events under the lock to advance the ring.
        lock (team.StateLock)
        {
            for (var i = 0; i < 10; i++)
                team.Ring.Append(new Envelope<object>("E", default, null, null, null, i, $"e{i}"));
        }
        var resume = reg.TryRegister("alpha", 5);
        Assert.True(resume.Success);
        Assert.False(resume.ReregisterRequired);
        Assert.Equal(5, resume.ResumedFromSequence);
    }

    [Fact]
    public void TryRegister_ResumeOutsideRetention_RequiresReregister()
    {
        var reg = NewRegistry();
        var first = reg.TryRegister("alpha", 0);
        var team = first.TeamState!;
        // Drain enough events to advance tail past 0. The ring is the
        // production-default RingBuffer.DefaultCapacity (65536), so iterate
        // a small margin past it to push tail strictly above 10.
        var iterations = RingBuffer.DefaultCapacity + 100;
        lock (team.StateLock)
        {
            for (var i = 0; i < iterations; i++)
                team.Ring.Append(new Envelope<object>("E", default, null, null, null, i, $"e{i}"));
        }
        var resume = reg.TryRegister("alpha", 10);   // 10 < tail
        Assert.True(resume.Success);
        Assert.True(resume.ReregisterRequired);
    }

    [Fact]
    public void SnapshotAll_ReturnsTeamsSortedOrdinal()
    {
        var reg = NewRegistry();
        reg.TryRegister("charlie", 0);
        reg.TryRegister("alpha", 0);
        reg.TryRegister("bravo", 0);
        var snap = reg.SnapshotAll();
        Assert.Equal(3, snap.Length);
        Assert.Equal("alpha", snap[0].TeamName);
        Assert.Equal("bravo", snap[1].TeamName);
        Assert.Equal("charlie", snap[2].TeamName);
    }

    [Fact]
    public void OnSettledToIterationOpen_WipesEveryRing()
    {
        var reg = NewRegistry();
        var alpha = reg.TryRegister("alpha", 0).TeamState!;
        var bravo = reg.TryRegister("bravo", 0).TeamState!;
        lock (alpha.StateLock) { for (var i = 0; i < 5; i++) alpha.Ring.Append(NewEnv(i)); }
        lock (bravo.StateLock) { for (var i = 0; i < 7; i++) bravo.Ring.Append(NewEnv(i)); }

        reg.OnSettledToIterationOpen();

        Assert.Equal(0, alpha.Ring.Head);
        Assert.Equal(0, alpha.Ring.Tail);
        Assert.Equal(0, bravo.Ring.Head);
        Assert.Equal(0, bravo.Ring.Tail);
    }

    private static Envelope<object> NewEnv(int seq) =>
        new("E", default, null, null, null, seq, $"e{seq}");
}
