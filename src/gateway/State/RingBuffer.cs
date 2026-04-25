using Bifrost.Contracts.Internal;

namespace Bifrost.Gateway.State;

/// <summary>
/// Power-of-two array-backed ring buffer of envelope payloads.
/// CALLER INVARIANT: every public member must be invoked with the owning
/// TeamState.StateLock held. SnapshotFrom returns a copy so the caller can
/// release the lock before pushing the slice to the outbound Channel
/// (07-RESEARCH.md Pitfall 10).
/// </summary>
public sealed class RingBuffer
{
    public const int DefaultCapacity = 1 << 16;   // 65 536; D-10

    private readonly Envelope<object>[] _buf;
    private readonly int _mask;
    private long _head;   // next-write sequence (monotonic)
    private long _tail;   // oldest-retained sequence (monotonic)

    public RingBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of two.", nameof(capacity));
        _buf = new Envelope<object>[capacity];
        _mask = capacity - 1;
    }

    public long Capacity => _buf.LongLength;
    public long Head => _head;
    public long Tail => _tail;

    /// <summary>CALLER holds StateLock. Returns assigned sequence (monotonic from 0).</summary>
    public long Append(Envelope<object> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var seq = _head;
        _buf[seq & _mask] = envelope;
        _head = seq + 1;
        if (_head - _tail > _buf.LongLength)
            _tail = _head - _buf.LongLength;
        return seq;
    }

    /// <summary>
    /// CALLER holds StateLock. Returns the slice (resumeFromSequence, head-1] as a fresh array.
    /// Empty array if resumeFromSequence >= head-1.
    /// </summary>
    public Envelope<object>[] SnapshotFrom(long resumeFromSequence)
    {
        if (resumeFromSequence >= _head - 1) return Array.Empty<Envelope<object>>();
        var startSeq = Math.Max(resumeFromSequence + 1, _tail);
        var count = (int)(_head - startSeq);
        if (count <= 0) return Array.Empty<Envelope<object>>();
        var copy = new Envelope<object>[count];
        for (var i = 0; i < count; i++)
            copy[i] = _buf[(startSeq + i) & _mask];
        return copy;
    }

    /// <summary>D-11: Settled→IterationOpen wipe. CALLER holds StateLock.</summary>
    public void Wipe()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _head = 0;
        _tail = 0;
    }

    /// <summary>True iff sequence is within the current retention window [tail, head).</summary>
    public bool IsRetained(long sequence) => sequence >= _tail && sequence < _head;
}
