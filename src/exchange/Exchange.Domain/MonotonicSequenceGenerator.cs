namespace Bifrost.Exchange.Domain;

public sealed class MonotonicSequenceGenerator(long startAt = 0) : ISequenceGenerator
{
    private long _current = startAt;

    public SequenceNumber Next() => new(++_current);
}
