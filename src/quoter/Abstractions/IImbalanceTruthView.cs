using Bifrost.Exchange.Domain;

namespace Bifrost.Quoter.Abstractions;

/// <summary>
/// Seam exposing the noiseless <c>P_imb_true</c> view of the imbalance simulator to the
/// quoter's blended fair-value computation. The wave-2 <see cref="Mocks.MockImbalanceTruthView"/>
/// returns a configurable constant; the production implementation arrives in the next phase
/// and computes <c>S + γ_regime · f(A_total)</c> using the simulator's perfect view of the
/// aggregated team and physical position, without the public-forecast noise term ε.
/// </summary>
public interface IImbalanceTruthView
{
    /// <summary>
    /// Returns the noiseless P_imb_true for <paramref name="instrument"/> at the current
    /// moment, expressed in price ticks.
    /// </summary>
    long GetTruePriceTicks(InstrumentId instrument);
}
