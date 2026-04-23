using Bifrost.Recorder.Session;
using Bifrost.Time;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Recorder.Tests;

/// <summary>
/// REC-01 ancillary: the three shutdown classifications
/// (graceful / timeout / crash) map deterministically to the state of the
/// cancellation token and the last-event-time timestamp. Each fact drives a
/// FakeTimeProvider so no wall-clock flake can sneak in.
/// </summary>
public sealed class ExitReasonDetectorTests
{
    private sealed class FakeClock(FakeTimeProvider provider) : IClock
    {
        public DateTimeOffset GetUtcNow() => provider.GetUtcNow();
    }

    [Fact]
    public void Detect_ReturnsGraceful_WhenCancellationRequested()
    {
        var prov = new FakeTimeProvider();
        var d = new ExitReasonDetector(new FakeClock(prov), TimeSpan.FromSeconds(10));

        Assert.Equal("graceful", d.Detect(cancellationRequested: true));
    }

    [Fact]
    public void Detect_ReturnsTimeout_WhenNoEventsWithinWindow()
    {
        var prov = new FakeTimeProvider();
        var d = new ExitReasonDetector(new FakeClock(prov), TimeSpan.FromSeconds(5));

        // Prime the last-event time; note ExitReasonDetector's ctor also stamps
        // _lastEventTime via the clock so this is belt-and-braces.
        d.OnEventReceived();

        // Advance beyond the timeout threshold.
        prov.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("timeout", d.Detect(cancellationRequested: false));
    }

    [Fact]
    public void Detect_ReturnsCrash_WhenEventsRecentButNoCancel()
    {
        var prov = new FakeTimeProvider();
        var d = new ExitReasonDetector(new FakeClock(prov), TimeSpan.FromSeconds(30));

        d.OnEventReceived();

        // Stay well inside the 30-s timeout window.
        prov.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("crash", d.Detect(cancellationRequested: false));
    }
}
