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

    public TeamState(string teamName, string clientId, DateTimeOffset registeredAtUtc, int ringCapacity = RingBuffer.DefaultCapacity)
    {
        TeamName = teamName ?? throw new ArgumentNullException(nameof(teamName));
        ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        Ring = new RingBuffer(ringCapacity);
        var slots = InstrumentOrdering.Slots;
        NetPositionTicks = new long[slots];
        VwapTicks = new long[slots];
        OpenOrdersNotionalTicks = new long[slots];
        RegisteredAtUtc = registeredAtUtc;
        RateLimitedUntilUtc = DateTimeOffset.MinValue;
    }
}
