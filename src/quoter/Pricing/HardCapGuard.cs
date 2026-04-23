namespace Bifrost.Quoter.Pricing;

/// <summary>
/// Per-instrument quoting directive emitted by <see cref="HardCapGuard"/>. Each side flag
/// indicates whether the quoter should publish quotes on that side this tick.
/// </summary>
public readonly record struct InventoryDirective(bool QuoteBids, bool QuoteAsks);

/// <summary>
/// Pure-math inventory guardrail. Implements the no-skew, one-sided suppression policy:
/// when net position breaches the configured cap, the side that would worsen the position
/// is suppressed (no asks while long, no bids while short); both sides resume only after
/// net position returns to within the hysteresis release band.
/// </summary>
public static class HardCapGuard
{
    /// <summary>
    /// Computes the next quoting directive given the current net position, the activation
    /// cap, the hysteresis release threshold, and the previous tick's directive.
    /// </summary>
    /// <param name="netPosition">Current net position for the instrument (positive = long).</param>
    /// <param name="maxNetPosition">Activation threshold; expected positive.</param>
    /// <param name="hardCapRelease">
    /// Hysteresis release threshold; expected to satisfy
    /// <c>0 &lt;= hardCapRelease &lt;= maxNetPosition</c>.
    /// </param>
    /// <param name="previous">Directive from the previous tick (used for hysteresis).</param>
    public static InventoryDirective Evaluate(
        decimal netPosition,
        decimal maxNetPosition,
        decimal hardCapRelease,
        InventoryDirective previous)
    {
        // Activation always wins over hysteresis: a fresh breach immediately suppresses
        // the offending side regardless of previous state.
        if (netPosition > maxNetPosition)
            return new InventoryDirective(QuoteBids: true, QuoteAsks: false);

        if (netPosition < -maxNetPosition)
            return new InventoryDirective(QuoteBids: false, QuoteAsks: true);

        // Inside the band: release suppression only after position has retreated to the
        // hysteresis threshold; otherwise preserve the previous tick's directive.
        if (!previous.QuoteAsks && netPosition <= hardCapRelease)
            return new InventoryDirective(QuoteBids: true, QuoteAsks: true);

        if (!previous.QuoteBids && netPosition >= -hardCapRelease)
            return new InventoryDirective(QuoteBids: true, QuoteAsks: true);

        return previous;
    }
}
