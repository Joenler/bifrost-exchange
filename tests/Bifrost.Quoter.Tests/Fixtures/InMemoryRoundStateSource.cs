using Bifrost.Exchange.Application.RoundState;

namespace Bifrost.Quoter.Tests.Fixtures;

/// <summary>
/// Mutable test-only <see cref="IRoundStateSource"/> for the Quoter integration
/// suite. Authored fresh in this assembly (the Phase 02 Bifrost.Exchange.Tests
/// variant lives in a different test assembly with a different namespace and
/// requires an injected IClock that this fixture intentionally does not need
/// because the Quoter's RoundState gate cares only about <c>Current</c>).
/// <para>
/// <see cref="Transition"/> mutates <c>Current</c> and fires <c>OnChange</c>;
/// the <c>TimestampNs</c> on the event is a deterministic 0 because Quoter's
/// gate consults <c>Current</c> only, not the timestamp.
/// </para>
/// </summary>
public sealed class InMemoryRoundStateSource : IRoundStateSource
{
    private RoundState _current;

    public InMemoryRoundStateSource(RoundState initial)
    {
        _current = initial;
    }

    public RoundState Current => _current;

    public event EventHandler<RoundStateChangedEventArgs>? OnChange;

    /// <summary>
    /// Switch to <paramref name="next"/>, raising <see cref="OnChange"/> with
    /// the previous and new states. No-ops when <paramref name="next"/> equals
    /// the current value (matches the Phase 02 Set semantic).
    /// </summary>
    public void Transition(RoundState next)
    {
        if (_current == next)
            return;
        var previous = _current;
        _current = next;
        OnChange?.Invoke(this, new RoundStateChangedEventArgs(previous, next, TimestampNs: 0L));
    }
}
