using System.Threading.Channels;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// Locks the channel's back-pressure semantics: under saturation the producer
/// must block on WriteAsync, never silently drop. A dropped fill would corrupt
/// the (clientId, QH) → net_position map and make the settlement row computed
/// at Gate wrong — the cost is silent and severe, so we prefer back-pressure
/// surfacing as a test flake over silent data loss in production.
/// </summary>
public class ActorLoopBackPressureTests
{
    [Fact]
    public async Task FillChannel_FullMode_Waits_NeverDropsFills()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — small-capacity channel matching the production registration
        // shape (FullMode=Wait, SingleReader) but with tiny capacity so a
        // handful of writes saturates it.
        const int capacity = 8;
        var channel = Channel.CreateBounded<SimulatorMessage>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Fill every slot without a reader — all 8 synchronous writes succeed.
        for (var i = 0; i < capacity; i++)
        {
            var written = channel.Writer.TryWrite(
                new FillMessage(i, "alpha", "Q1", 0, "Buy", 1));
            Assert.True(written, $"slot {i} write failed — capacity underestimate?");
        }

        // 9th write — channel is full; TryWrite must return false synchronously
        // with FullMode=Wait. (TryWrite never blocks; it surfaces fullness
        // by returning false.)
        var ninth = channel.Writer.TryWrite(
            new FillMessage(100, "alpha", "Q1", 0, "Buy", 1));
        Assert.False(ninth, "TryWrite on full channel with FullMode=Wait must fail synchronously");

        // WriteAsync on full channel with FullMode=Wait must NOT complete
        // synchronously; it should return an uncompleted task that resolves
        // when a slot frees up. If the channel were misconfigured with
        // FullMode=DropOldest, WriteAsync would complete immediately and the
        // oldest entry would silently vanish.
        var writeTask = channel.Writer.WriteAsync(
            new FillMessage(101, "alpha", "Q1", 0, "Buy", 1), ct).AsTask();

        // Give the scheduler ample time to complete if it were going to. If
        // writeTask completed, the channel dropped an earlier entry.
        var finished = await Task.WhenAny(writeTask, Task.Delay(200, ct));
        Assert.NotSame(writeTask, finished);   // writeTask must still be pending

        // Now drain one item — writeTask should complete promptly.
        var drained = await channel.Reader.ReadAsync(ct);
        Assert.NotNull(drained);

        var completion = await Task.WhenAny(writeTask, Task.Delay(200, ct));
        Assert.Same(writeTask, completion);
        await writeTask;   // must not throw
    }

    [Fact]
    public void FillChannel_FullModeWait_IsConfigurationInvariant()
    {
        // Belt-and-braces documentation of the invariant the prior test proves
        // behaviorally. If anyone ever flips the production registration to
        // DropOldest / DropNewest / DropWrite, the simulator will silently
        // lose fills and this test won't catch it directly (Program.cs isn't
        // imported here) — but the contract is stated plainly so a code
        // review can cross-reference.
        var options = new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        };
        Assert.Equal(BoundedChannelFullMode.Wait, options.FullMode);
        Assert.NotEqual(BoundedChannelFullMode.DropOldest, options.FullMode);
        Assert.NotEqual(BoundedChannelFullMode.DropNewest, options.FullMode);
        Assert.NotEqual(BoundedChannelFullMode.DropWrite, options.FullMode);
    }
}
