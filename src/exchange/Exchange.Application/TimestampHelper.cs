namespace Bifrost.Exchange.Application;

public static class TimestampHelper
{
    private static readonly long EpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

    public static long ToUnixNanoseconds(DateTimeOffset timestamp)
    {
        return (timestamp.UtcTicks - EpochTicks) * 100;
    }
}
