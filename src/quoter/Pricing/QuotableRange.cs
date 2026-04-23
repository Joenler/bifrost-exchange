namespace Bifrost.Quoter.Pricing;

public readonly record struct QuotableRange
{
    public long BidTicks { get; }
    public long AskTicks { get; }

    public QuotableRange(long bidTicks, long askTicks)
    {
        if (bidTicks >= askTicks)
            throw new ArgumentException(
                $"Crossed quotable range: bid ({bidTicks}) >= ask ({askTicks}).");

        BidTicks = bidTicks;
        AskTicks = askTicks;
    }
}
