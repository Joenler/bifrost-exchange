using Bifrost.Time;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Orchestrator.Tests.TestSupport;

/// <summary>
/// <see cref="IClock"/> wrapper around
/// <see cref="FakeTimeProvider"/>. Mirrors the Exchange test-suite's
/// <c>TestClock</c> fixture (see tests/Bifrost.Exchange.Tests/Fixtures/TestClock.cs)
/// so the orchestrator's actor-loop, timer, and heartbeat tests can drive
/// virtual time deterministically via <see cref="Advance"/> and
/// <see cref="SetUtcNow"/>.
/// </summary>
public sealed class FakeClock : IClock
{
    public FakeTimeProvider Provider { get; }

    public FakeClock(DateTimeOffset? start = null)
    {
        Provider = new FakeTimeProvider(
            start ?? new DateTimeOffset(2026, 4, 24, 0, 0, 0, TimeSpan.Zero));
    }

    public DateTimeOffset GetUtcNow() => Provider.GetUtcNow();

    public void Advance(TimeSpan delta) => Provider.Advance(delta);

    public void SetUtcNow(DateTimeOffset value) => Provider.SetUtcNow(value);
}
