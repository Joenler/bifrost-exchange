namespace Bifrost.Exchange.Domain;

public readonly record struct DeliveryPeriod
{
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }

    public DeliveryPeriod(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();

        if (Start >= End)
            throw new ArgumentException("Delivery period start must be before end.");

        var duration = End - Start;
        if (duration != TimeSpan.FromMinutes(15) && duration != TimeSpan.FromMinutes(60))
            throw new ArgumentException($"Delivery period duration must be exactly 15 or 60 minutes, was {duration.TotalMinutes} minutes.");
    }

    public bool HasExpired(DateTimeOffset utcNow) => utcNow >= End;

    public override string ToString() => $"{Start:yyyyMMddHHmm}-{End:yyyyMMddHHmm}";
}
