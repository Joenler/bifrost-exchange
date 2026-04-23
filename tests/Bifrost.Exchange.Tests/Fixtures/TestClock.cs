using Bifrost.Time;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Exchange.Tests.Fixtures;

/// <summary>
/// Bifrost.Time.IClock adapter backed by FakeTimeProvider for deterministic tests.
/// Advance virtual time via Provider.Advance(…); read via GetUtcNow().
/// </summary>
public sealed class TestClock : IClock
{
    private readonly FakeTimeProvider _provider;

    public TestClock(FakeTimeProvider? provider = null)
    {
        _provider = provider ?? new FakeTimeProvider();
    }

    public DateTimeOffset GetUtcNow() => _provider.GetUtcNow();

    public FakeTimeProvider Provider => _provider;
}
