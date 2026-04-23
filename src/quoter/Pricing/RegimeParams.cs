namespace Bifrost.Quoter.Pricing;

/// <summary>
/// The five tunable knobs the regime schedule applies on each transition.
/// <list type="bullet">
///   <item><description><see cref="SpreadMultiplier"/> scales the Avellaneda-Stoikov half-spread.</description></item>
///   <item><description><see cref="QuantityMultiplier"/> scales the per-level base quantity (quoter pulls back under stress).</description></item>
///   <item><description><see cref="GbmDrift"/> and <see cref="GbmVol"/> feed the per-tick GBM step.</description></item>
///   <item><description><see cref="Kappa"/> is the order-arrival intensity used by the half-spread formula.</description></item>
/// </list>
/// </summary>
public sealed record RegimeParams(
    double SpreadMultiplier,
    double QuantityMultiplier,
    double GbmDrift,
    double GbmVol,
    double Kappa);
