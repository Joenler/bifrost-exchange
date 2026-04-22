namespace Bifrost.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
}
