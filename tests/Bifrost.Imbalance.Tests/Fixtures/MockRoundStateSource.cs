using Bifrost.Exchange.Application.RoundState;
using Bifrost.Time;

namespace Bifrost.Imbalance.Tests.Fixtures;

/// <summary>
/// Test-only mutable <see cref="IRoundStateSource"/>. Raises
/// <see cref="OnChange"/> synchronously so the subscribing bridge is driven on
/// the same thread as the test — there is no thread hop to wait on and no
/// deadlock risk. Mirrors <c>Bifrost.Exchange.Tests.RoundState.InMemoryRoundStateSource</c>
/// but lives under the simulator test namespace so it cannot be reached from
/// production code.
/// </summary>
public sealed class MockRoundStateSource : IRoundStateSource
{
    private readonly IClock _clock;
    private RoundState _current;

    public RoundState Current => _current;

    public event EventHandler<RoundStateChangedEventArgs>? OnChange;

    public MockRoundStateSource(IClock clock, RoundState initial = RoundState.IterationOpen)
    {
        _clock = clock;
        _current = initial;
    }

    /// <summary>
    /// Transition to <paramref name="next"/>, raising <see cref="OnChange"/>
    /// with the previous and new state. No-op if <paramref name="next"/>
    /// equals the current value.
    /// </summary>
    public void Set(RoundState next)
    {
        if (_current == next) return;
        var previous = _current;
        _current = next;
        var ts = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000;
        OnChange?.Invoke(this, new RoundStateChangedEventArgs(previous, next, ts));
    }

    /// <summary>
    /// Walk the full 7-state cycle
    /// IterationOpen -> AuctionOpen -> AuctionClosed -> RoundOpen -> Gate ->
    /// Settled -> IterationOpen, advancing the clock by
    /// <paramref name="perState"/> between transitions. A short real-time
    /// yield after each Advance lets async continuations run before the next
    /// transition fires.
    /// </summary>
    public async Task DriveFullCycleAsync(FakeClock clock, TimeSpan perState, CancellationToken ct = default)
    {
        var sequence = new[]
        {
            RoundState.IterationOpen, RoundState.AuctionOpen, RoundState.AuctionClosed,
            RoundState.RoundOpen, RoundState.Gate, RoundState.Settled, RoundState.IterationOpen,
        };

        foreach (var s in sequence)
        {
            Set(s);
            clock.Provider.Advance(perState);
            await Task.Delay(1, ct);
        }
    }
}
