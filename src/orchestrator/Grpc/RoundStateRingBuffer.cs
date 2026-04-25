using System.Collections.Concurrent;
using System.Threading.Channels;
using Bifrost.Contracts.Internal.Events;

namespace Bifrost.Orchestrator.Grpc;

/// <summary>
/// Bounded in-memory ring buffer of the most-recent
/// <see cref="RoundStateChangedPayload"/> snapshots — capacity 128 per CONTEXT
/// D-15. Not persisted across restart: that is intentional. A fresh boot
/// publishes a single reconciliation snapshot on
/// <c>bifrost.round.v1</c> (see <c>OrchestratorActor.ExecuteAsync</c>) which
/// re-seeds the ring; subscribers reconnect through <c>WatchRoundState</c>
/// and receive the synthetic resume-reset.
/// </summary>
/// <remarks>
/// Subscribers register via <see cref="Subscribe"/> and consume snapshots from
/// the returned <see cref="ChannelReader{T}"/>. On disconnect the
/// <see cref="IDisposable"/> unregisters and completes the subscriber's
/// channel writer.
///
/// Thread-safety: <see cref="AppendSnapshot"/> may be called from the actor
/// drain loop concurrently with <see cref="Subscribe"/> + dispose calls
/// running on gRPC handler threads. The ring writes are guarded by
/// <c>_gate</c>; the subscriber map uses <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// for add/remove (compound read-modify-write is NEVER performed on a
/// subscriber entry — Add then Remove are independent operations, which the
/// repo-wide BannedSymbols fence permits because subscriber tracking is not
/// scoring state).
/// </remarks>
public sealed class RoundStateRingBuffer
{
    public const int Capacity = 128;

    private readonly object _gate = new();
    private readonly RoundStateChangedPayload?[] _buffer = new RoundStateChangedPayload?[Capacity];
    private int _count;
    private int _writeIndex;

    private readonly ConcurrentDictionary<int, Subscriber> _subscribers = new();
    private int _nextSubscriberId;

    /// <summary>
    /// Append <paramref name="payload"/> to the ring and fan it out to all
    /// active subscribers. Called by the orchestrator actor immediately
    /// after a successful <c>PublishRoundStateChangedAsync</c> publish.
    /// </summary>
    public void AppendSnapshot(RoundStateChangedPayload payload)
    {
        lock (_gate)
        {
            _buffer[_writeIndex] = payload;
            _writeIndex = (_writeIndex + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }

        // Fan-out happens OUTSIDE the lock so a slow subscriber can never
        // block a producer (TryWrite on an unbounded channel never waits).
        foreach (Subscriber sub in _subscribers.Values)
        {
            sub.Channel.Writer.TryWrite(payload);
        }
    }

    /// <summary>
    /// Snapshot of every payload currently in the ring, in chronological
    /// order (oldest first, newest last). Used by <c>WatchRoundState</c>
    /// to replay the tail to a resuming client.
    /// </summary>
    public IReadOnlyList<RoundStateChangedPayload> SnapshotInOrder()
    {
        lock (_gate)
        {
            List<RoundStateChangedPayload> list = new(_count);
            int start = _count < Capacity ? 0 : _writeIndex;
            for (int i = 0; i < _count; i++)
            {
                RoundStateChangedPayload? p = _buffer[(start + i) % Capacity];
                if (p is not null)
                {
                    list.Add(p);
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Most-recently-appended snapshot, or <c>null</c> if the ring is
    /// empty. Used as the synthetic-resume-reset payload when a
    /// <c>WatchRoundState</c> resume request falls outside the ring.
    /// </summary>
    public RoundStateChangedPayload? Current()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return null;
            }
            int lastIdx = (_writeIndex - 1 + Capacity) % Capacity;
            return _buffer[lastIdx];
        }
    }

    /// <summary>
    /// Subscribe to future snapshots. Returns a disposable that
    /// unregisters the subscriber and completes its channel writer when
    /// the caller (the gRPC handler) disposes it — typically via a
    /// <c>using</c> block tied to the streaming RPC's lifetime.
    /// </summary>
    public IDisposable Subscribe(out ChannelReader<RoundStateChangedPayload> reader)
    {
        int id = Interlocked.Increment(ref _nextSubscriberId);
        Channel<RoundStateChangedPayload> ch = Channel.CreateUnbounded<RoundStateChangedPayload>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        Subscriber sub = new(id, ch);
        _subscribers[id] = sub;
        reader = ch.Reader;
        return new UnsubscribeHandle(_subscribers, id, ch);
    }

    private sealed record Subscriber(int Id, Channel<RoundStateChangedPayload> Channel);

    private sealed class UnsubscribeHandle : IDisposable
    {
        private readonly ConcurrentDictionary<int, Subscriber> _subs;
        private readonly int _id;
        private readonly Channel<RoundStateChangedPayload> _ch;
        private bool _disposed;

        public UnsubscribeHandle(
            ConcurrentDictionary<int, Subscriber> subs,
            int id,
            Channel<RoundStateChangedPayload> ch)
        {
            _subs = subs;
            _id = id;
            _ch = ch;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _subs.TryRemove(_id, out _);
            _ch.Writer.TryComplete();
        }
    }
}
