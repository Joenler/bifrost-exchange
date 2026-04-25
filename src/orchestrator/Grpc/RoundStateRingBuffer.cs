using System.Threading.Channels;
using Bifrost.Contracts.Internal.Events;

namespace Bifrost.Orchestrator.Grpc;

/// <summary>
/// Stub shell for the actor-loop wiring plan. The follow-up gRPC plan fills in
/// the subscribe/replay behaviour (128-capacity ring, Subscribe + Unsubscribe,
/// SnapshotInOrder). Constructor + public method surface is LOCKED here — the
/// follow-up plan MUST NOT widen or change any public signature.
/// </summary>
public sealed class RoundStateRingBuffer
{
    public const int Capacity = 128;

    public RoundStateRingBuffer()
    {
    }

    /// <summary>No-op in this plan. Follow-up gRPC plan fills in.</summary>
    public void AppendSnapshot(RoundStateChangedPayload payload)
    {
    }

    /// <summary>Returns null in this plan. Follow-up gRPC plan fills in.</summary>
    public RoundStateChangedPayload? Current() => null;

    /// <summary>Empty list in this plan. Follow-up gRPC plan fills in.</summary>
    public IReadOnlyList<RoundStateChangedPayload> SnapshotInOrder() =>
        Array.Empty<RoundStateChangedPayload>();

    /// <summary>
    /// Follow-up gRPC plan fills in the real subscribe path. Stub returns an
    /// unsubscribed reader + a no-op handle so the method signature compiles.
    /// </summary>
    public IDisposable Subscribe(out ChannelReader<RoundStateChangedPayload> reader)
    {
        Channel<RoundStateChangedPayload> ch = Channel.CreateUnbounded<RoundStateChangedPayload>();
        reader = ch.Reader;
        return new NoOpHandle(ch);
    }

    private sealed class NoOpHandle : IDisposable
    {
        private readonly Channel<RoundStateChangedPayload> _ch;

        public NoOpHandle(Channel<RoundStateChangedPayload> ch)
        {
            _ch = ch;
        }

        public void Dispose() => _ch.Writer.TryComplete();
    }
}
