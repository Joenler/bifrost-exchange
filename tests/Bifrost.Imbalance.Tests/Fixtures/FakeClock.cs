using Bifrost.Time;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Imbalance.Tests.Fixtures;

/// <summary>
/// <see cref="IClock"/> adapter backed by <see cref="FakeTimeProvider"/> for
/// deterministic tests. Advance virtual time via <c>Provider.Advance(...)</c>;
/// read via <see cref="GetUtcNow"/>. Mirrors <c>Bifrost.Exchange.Tests.Fixtures.TestClock</c>.
/// </summary>
public sealed class FakeClock : IClock
{
    private readonly FakeTimeProvider _provider;

    public FakeClock(FakeTimeProvider? provider = null)
    {
        _provider = provider ?? new FakeTimeProvider();
    }

    public DateTimeOffset GetUtcNow() => _provider.GetUtcNow();

    public FakeTimeProvider Provider => _provider;
}
