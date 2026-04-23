using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Abstractions;

namespace Bifrost.Quoter.Mocks;

/// <summary>
/// Constant-returning <see cref="IImbalanceTruthView"/> stand-in used while the real
/// imbalance simulator is offline. Returns the same configured tick price for every
/// instrument; the production implementation lands alongside the imbalance simulator
/// and replaces this binding without touching call sites.
/// </summary>
public sealed class MockImbalanceTruthView : IImbalanceTruthView
{
    private readonly long _constantPriceTicks;

    public MockImbalanceTruthView(long constantPriceTicks)
    {
        _constantPriceTicks = constantPriceTicks;
    }

    public long GetTruePriceTicks(InstrumentId instrument) => _constantPriceTicks;
}
