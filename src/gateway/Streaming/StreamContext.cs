using System.Diagnostics;
using System.Threading.Channels;
using Bifrost.Gateway.State;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Streaming;

/// <summary>
/// Per-bidi-stream context. Holds the resolved <see cref="State.TeamState"/>,
/// the per-stream bounded outbound <see cref="Channel{T}"/>, the linked-to-RPC
/// <see cref="CancellationTokenSource"/>, and an observation-only
/// <see cref="System.Diagnostics.Stopwatch"/> the inbound-command latency
/// histogram reads (Plan 08).
///
/// 07-RESEARCH §Pattern 1 + Pitfall 2: ALL outbound traffic is marshalled
/// through <see cref="Outbound"/>; only the writer task spawned in
/// <c>StrategyGatewayService.StreamStrategy</c> calls
/// <c>responseStream.WriteAsync</c>. The bounded channel
/// (capacity 1024 by default; <see cref="BoundedChannelFullMode.Wait"/>;
/// <see cref="BoundedChannelOptions.SingleReader"/> = true) gives back-pressure
/// to producers (RabbitMQ consumers, ForecastDispatcher, the bidi reader's own
/// reject path) without ever letting two writers race the wire.
/// </summary>
public sealed class StreamContext : IDisposable
{
    public TeamState TeamState { get; }

    public Channel<StrategyProto.MarketEvent> Outbound { get; }

    public CancellationTokenSource StreamCts { get; }

    public Stopwatch StreamStopwatch { get; } = Stopwatch.StartNew();

    public StreamContext(TeamState teamState, int outboundCapacity, CancellationToken parentCt)
    {
        TeamState = teamState ?? throw new ArgumentNullException(nameof(teamState));
        if (outboundCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(outboundCapacity), "outboundCapacity must be > 0");

        Outbound = Channel.CreateBounded<StrategyProto.MarketEvent>(new BoundedChannelOptions(outboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        StreamCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
    }

    public void Dispose()
    {
        StreamCts.Dispose();
    }
}
