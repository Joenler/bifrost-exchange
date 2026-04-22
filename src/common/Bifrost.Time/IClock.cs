namespace Bifrost.Time;

public interface IClock
{
    DateTimeOffset GetUtcNow();
}
