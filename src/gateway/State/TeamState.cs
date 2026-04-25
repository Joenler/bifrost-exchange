// LOCK ORDER: TeamRegistry.Lock → teamState[i].StateLock (i = team_name string-ordinal ascending).
// Cross-team operations (Settled→IterationOpen ring-buffer wipe, mass-cancel-many) acquire ALL
// per-team locks in team_name-ascending string-ordinal order AFTER releasing the registry lock.
// Violating this order risks deadlock vs the reader-task inbound command path.
//
// CONSUMER CONTRACT: RabbitMQ AsyncEventingBasicConsumer callbacks acquire the target
// teamState.StateLock BEFORE calling teamState.Ring.Append(envelope) and RELEASE the lock
// BEFORE writing to teamState.Outbound (Pitfall 10). They do NOT acquire TeamRegistry.Lock —
// the registry lock is held only at Register/Disconnect/SettledWipe boundaries.
//
// RING BUFFER invariants (held under teamState.StateLock):
//   - head, tail are monotonic longs
//   - capacity = 65536 (power of two; enforced at ctor)
//   - envelope[i & 0xFFFF] is valid iff (tail <= i < head)
//   - D-11: Settled→IterationOpen wipe resets head = tail = 0 atomically
//
// POSITION invariants (held under teamState.StateLock):
//   - NetPositionTicks[inst] = Σ signed filled-quantity-ticks per instrument
//   - VwapTicks[inst] = VWAP over fills on instrument (running weighted mean)
//   - OpenOrdersNotionalTicks[inst] = Σ unfilled qty × price of resting orders
//
// GUARD STATE invariants (held under teamState.StateLock; Plan 07-04):
//   - OpenOrdersByInstrument[inst] reflects ADR-0004 #2 (max-open-orders) live counts.
//   - OtrSubmitsWindow / OtrTradesWindow are rolling-window queues for ADR-0004 #1 (OTR);
//     OtrGuard trims entries older than thresholds.OtrWindowSeconds before evaluation.
//   - MsgRateWindow is the 1s rolling window for ADR-0004 #6 (gateway msg rate); MsgRateGuard
//     trims entries older than 1s before evaluation.
//   - RateLimitedUntilUtc is set by OtrGuard / MsgRateGuard on breach; subsequent commands
//     short-circuit at MsgRateGuard's boundary check until clock passes the gate.

using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.State;

public sealed class TeamState
{
    public readonly object StateLock = new();

    public string TeamName { get; }
    public string ClientId { get; }
    public RingBuffer Ring { get; }
    public long[] NetPositionTicks { get; }
    public long[] VwapTicks { get; }
    public long[] OpenOrdersNotionalTicks { get; }
    public DateTimeOffset RegisteredAtUtc { get; }
    public DateTimeOffset RateLimitedUntilUtc { get; set; }

    /// <summary>
    /// Open-orders map per instrument index (0..4 by InstrumentOrdering). Plain
    /// List&lt;OpenOrder&gt; is fine — typical team has &lt; 50 open orders per
    /// instrument. CALLER holds StateLock for any read or mutation.
    /// </summary>
    public List<OpenOrder>[] OpenOrdersByInstrument { get; }

    /// <summary>
    /// OTR rolling-window queue (submits + replaces). OtrGuard appends current time on
    /// each new submit/replace; trims entries older than OtrWindowSeconds before
    /// checking. CALLER holds StateLock.
    /// </summary>
    public Queue<DateTimeOffset> OtrSubmitsWindow { get; }

    /// <summary>
    /// OTR rolling-window queue (trades). Plan 06's PrivateEventConsumer calls
    /// OtrGuard.RecordTrade on every OrderExecutedEvent so the OTR denominator stays
    /// current. CALLER holds StateLock.
    /// </summary>
    public Queue<DateTimeOffset> OtrTradesWindow { get; }

    /// <summary>
    /// Msg-rate rolling-window queue. Trimmed at the 1-second boundary on each call.
    /// CALLER holds StateLock.
    /// </summary>
    public Queue<DateTimeOffset> MsgRateWindow { get; }

    /// <summary>
    /// Outbound channel attached at stream-open. Plan 05 attaches; Plan 07 mass-cancel
    /// reads. Null when no stream is connected. CALLER holds StateLock for attach/detach;
    /// the channel writer itself is thread-safe and may be written to without the
    /// StateLock (Pitfall 10 — release StateLock before writing to Outbound).
    /// </summary>
    public System.Threading.Channels.ChannelWriter<StrategyProto.MarketEvent>? Outbound { get; private set; }

    public TeamState(string teamName, string clientId, DateTimeOffset registeredAtUtc, int ringCapacity = RingBuffer.DefaultCapacity)
    {
        TeamName = teamName ?? throw new ArgumentNullException(nameof(teamName));
        ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        Ring = new RingBuffer(ringCapacity);
        var slots = InstrumentOrdering.Slots;
        NetPositionTicks = new long[slots];
        VwapTicks = new long[slots];
        OpenOrdersNotionalTicks = new long[slots];
        OpenOrdersByInstrument = new List<OpenOrder>[slots];
        for (var i = 0; i < slots; i++) OpenOrdersByInstrument[i] = new List<OpenOrder>();
        OtrSubmitsWindow = new Queue<DateTimeOffset>();
        OtrTradesWindow = new Queue<DateTimeOffset>();
        MsgRateWindow = new Queue<DateTimeOffset>();
        RegisteredAtUtc = registeredAtUtc;
        RateLimitedUntilUtc = DateTimeOffset.MinValue;
    }

    /// <summary>CALLER holds StateLock.</summary>
    public void AttachOutbound(System.Threading.Channels.ChannelWriter<StrategyProto.MarketEvent> writer) =>
        Outbound = writer ?? throw new ArgumentNullException(nameof(writer));

    /// <summary>CALLER holds StateLock.</summary>
    public void DetachOutbound() => Outbound = null;
}

/// <summary>
/// Per-instrument resting-order record held under <see cref="TeamState.StateLock"/>.
/// Side is the Phase 02 DTO string convention ("Buy" | "Sell") — matches
/// InboundTranslator.SideEnumToString output so consumers and guards share one shape.
/// </summary>
public sealed record OpenOrder(
    long OrderId,
    string ClientOrderId,
    int InstrumentIndex,
    string Side,
    long PriceTicks,
    long QuantityTicks,
    long DisplaySliceTicks,
    DateTimeOffset SubmittedAtUtc);
