using Bifrost.Time;

namespace Bifrost.Recorder.Session;

/// <summary>
/// Inspects observed event cadence + cancellation state at shutdown to classify
/// the recorder's exit as <c>graceful</c>, <c>timeout</c>, or <c>crash</c>.
/// The classification lands in the session manifest so post-event forensics
/// can tell a clean SIGTERM from a loss-of-heartbeat stall.
/// </summary>
public sealed class ExitReasonDetector
{
    private readonly IClock _clock;
    private readonly TimeSpan _timeout;
    private DateTimeOffset _lastEventTime;

    public ExitReasonDetector(IClock clock, TimeSpan timeout)
    {
        _clock = clock;
        _timeout = timeout;
        _lastEventTime = _clock.GetUtcNow();
    }

    public void OnEventReceived()
    {
        _lastEventTime = _clock.GetUtcNow();
    }

    public string Detect(bool cancellationRequested)
    {
        if (cancellationRequested)
            return "graceful";

        if (_clock.GetUtcNow() - _lastEventTime > _timeout)
            return "timeout";

        return "crash";
    }
}
