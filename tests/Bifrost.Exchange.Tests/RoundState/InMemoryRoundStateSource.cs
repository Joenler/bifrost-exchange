using Bifrost.Exchange.Application.RoundState;
using Bifrost.Time;

namespace Bifrost.Exchange.Tests.RoundState;

/// <summary>
/// Test-only mutable implementation of <see cref="IRoundStateSource"/>. Drives the
/// <see cref="OrderValidator"/> gate-guard through all 7 RoundState values in unit tests.
/// Namespace is deliberately <c>Bifrost.Exchange.Tests.RoundState</c> (not the production
/// <c>Bifrost.Exchange.Application.RoundState</c>) so it cannot be consumed by production
/// code.
/// </summary>
public sealed class InMemoryRoundStateSource : IRoundStateSource
{
    private readonly IClock _clock;
    private Application.RoundState.RoundState _current;

    public Application.RoundState.RoundState Current => _current;

    public event EventHandler<RoundStateChangedEventArgs>? OnChange;

    public InMemoryRoundStateSource(IClock clock, Application.RoundState.RoundState initial = Application.RoundState.RoundState.RoundOpen)
    {
        _clock = clock;
        _current = initial;
    }

    public void Set(Application.RoundState.RoundState next)
    {
        if (_current == next) return;
        var previous = _current;
        _current = next;
        var ts = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000;
        OnChange?.Invoke(this, new RoundStateChangedEventArgs(previous, next, ts));
    }
}
