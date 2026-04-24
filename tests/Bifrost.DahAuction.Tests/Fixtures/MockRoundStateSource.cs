using Bifrost.Exchange.Application.RoundState;
using Bifrost.Time;
using RoundStateEnum = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.DahAuction.Tests.Fixtures;

/// <summary>
/// Test-only mutable <see cref="IRoundStateSource"/>. Raises
/// <see cref="OnChange"/> synchronously on the caller's thread so the
/// integration tests can drive the auction lifecycle deterministically
/// without dealing with thread-hop timing.
/// </summary>
/// <remarks>
/// Lives under the DAH test namespace so production DI never accidentally
/// picks it up. Mirrors the analogous helpers under
/// <c>Bifrost.Imbalance.Tests.Fixtures</c> and
/// <c>Bifrost.Quoter.Tests.Fixtures</c>; not shared via InternalsVisibleTo so
/// each test project owns its own state-source fixture.
/// </remarks>
public sealed class MockRoundStateSource : IRoundStateSource
{
    private readonly IClock _clock;
    private RoundStateEnum _current;

    public RoundStateEnum Current => _current;
    public event EventHandler<RoundStateChangedEventArgs>? OnChange;

    public MockRoundStateSource(IClock clock, RoundStateEnum initial = RoundStateEnum.IterationOpen)
    {
        _clock = clock;
        _current = initial;
    }

    /// <summary>
    /// Transition to <paramref name="next"/>, raising <see cref="OnChange"/>
    /// synchronously on the caller's thread with the previous and new state.
    /// No-op if <paramref name="next"/> equals the current value.
    /// </summary>
    public void TransitionTo(RoundStateEnum next)
    {
        if (_current == next) return;
        var prev = _current;
        _current = next;
        long ts = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
        OnChange?.Invoke(this, new RoundStateChangedEventArgs(prev, next, ts));
    }
}
