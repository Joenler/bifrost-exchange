namespace Bifrost.Quoter.Pricing;

public readonly record struct HittableRange
{
    public long BidTicks { get; }
    public long AskTicks { get; }

    public HittableRange(long bidTicks, long askTicks)
    {
        if (bidTicks >= askTicks)
            throw new ArgumentException(
                $"Crossed hittable range: bid ({bidTicks}) >= ask ({askTicks}).");

        BidTicks = bidTicks;
        AskTicks = askTicks;
    }
}
