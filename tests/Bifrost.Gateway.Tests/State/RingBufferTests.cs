using Bifrost.Contracts.Internal;
using Bifrost.Gateway.State;
using Xunit;

namespace Bifrost.Gateway.Tests.State;

public class RingBufferTests
{
    [Fact]
    public void Ctor_NonPowerOfTwo_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RingBuffer(1000));
    }

    [Fact]
    public void Ctor_Zero_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RingBuffer(0));
    }

    [Fact]
    public void Append_AssignsMonotonicSequenceFromZero()
    {
        var rb = new RingBuffer(8);
        var s0 = rb.Append(MakeEnv(payload: "a"));
        var s1 = rb.Append(MakeEnv(payload: "b"));
        var s2 = rb.Append(MakeEnv(payload: "c"));
        Assert.Equal(0, s0);
        Assert.Equal(1, s1);
        Assert.Equal(2, s2);
        Assert.Equal(3, rb.Head);
        Assert.Equal(0, rb.Tail);
    }

    [Fact]
    public void SnapshotFrom_ReturnsSliceAfterResumePoint()
    {
        var rb = new RingBuffer(8);
        for (var i = 0; i < 5; i++) rb.Append(MakeEnv(payload: $"e{i}", sequence: i));
        var slice = rb.SnapshotFrom(1);    // resume after seq 1 → expect e2, e3, e4
        Assert.Equal(3, slice.Length);
        Assert.Equal(2L, slice[0].Sequence);
        Assert.Equal(4L, slice[2].Sequence);
    }

    [Fact]
    public void SnapshotFrom_ResumeAtHeadMinusOne_ReturnsEmpty()
    {
        var rb = new RingBuffer(8);
        rb.Append(MakeEnv(payload: "a"));
        rb.Append(MakeEnv(payload: "b"));
        var slice = rb.SnapshotFrom(1);    // head=2, resume=1 ⇒ empty
        Assert.Empty(slice);
    }

    [Fact]
    public void Append_BeyondCapacity_AdvancesTail()
    {
        var rb = new RingBuffer(4);
        for (var i = 0; i < 6; i++) rb.Append(MakeEnv(payload: $"e{i}", sequence: i));
        Assert.Equal(6, rb.Head);
        Assert.Equal(2, rb.Tail);   // oldest 2 evicted: tail = head - capacity = 6-4 = 2
        // Replay from a sequence below tail should clamp to tail.
        var slice = rb.SnapshotFrom(0);
        Assert.Equal(4, slice.Length);   // [2,3,4,5]
        Assert.Equal(2L, slice[0].Sequence);
    }

    [Fact]
    public void IsRetained_BelowTail_ReturnsFalse()
    {
        var rb = new RingBuffer(4);
        for (var i = 0; i < 8; i++) rb.Append(MakeEnv(payload: $"e{i}", sequence: i));
        Assert.False(rb.IsRetained(0));    // tail = 4
        Assert.False(rb.IsRetained(3));
        Assert.True(rb.IsRetained(4));
        Assert.True(rb.IsRetained(7));
        Assert.False(rb.IsRetained(8));    // head = 8 (next-write); not yet appended
    }

    [Fact]
    public void Wipe_ResetsHeadTailAndClearsBuffer()
    {
        var rb = new RingBuffer(4);
        for (var i = 0; i < 3; i++) rb.Append(MakeEnv(payload: $"e{i}"));
        rb.Wipe();
        Assert.Equal(0, rb.Head);
        Assert.Equal(0, rb.Tail);
        Assert.Empty(rb.SnapshotFrom(-1));
    }

    private static Envelope<object> MakeEnv(string payload, long? sequence = null) =>
        new("Test", new DateTimeOffset(2026, 4, 24, 0, 0, 0, TimeSpan.Zero), null, null, null, sequence, payload);
}
