namespace Bifrost.Imbalance;

/// <summary>
/// Lifetime of a physical shock once injected:
/// <list type="bullet">
///   <item><c>Round</c> — persists until Settled; contributes to A_physical all the way through Gate.</item>
///   <item><c>Transient</c> — contributes for a bounded window (configured via TTransientSeconds) then rolls off.</item>
/// </list>
/// </summary>
public enum ShockPersistence
{
    Round,
    Transient,
}
