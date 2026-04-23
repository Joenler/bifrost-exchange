namespace Bifrost.Quoter.Pricing;

/// <summary>
/// Client-assigned identifier for correlating order requests with exchange responses.
/// </summary>
public readonly record struct CorrelationId(string Value)
{
    public static CorrelationId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}
