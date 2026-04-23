using Bifrost.Exchange.Domain;

namespace Bifrost.Quoter.Pricing;

/// <summary>
/// Simulates fair prices for instruments using geometric Brownian motion.
/// Per-instrument independent RNG streams seeded deterministically from the
/// scenario seed via a Knuth-multiplier derivation.
/// Regime selection is OWNED EXTERNALLY: each call to <see cref="StepAll"/>
/// receives a <see cref="GbmParams"/> carrying the (drift, vol) chosen by the
/// caller's regime schedule.
/// </summary>
public sealed class GbmPriceModel
{
    private readonly GbmConfig _config;
    private readonly Dictionary<InstrumentId, int> _instrumentIndex;
    private readonly InstrumentState[] _instruments;
    private readonly InstrumentId[] _sortedInstruments;

    public GbmPriceModel(
        GbmConfig config,
        IReadOnlyList<InstrumentId> instruments,
        Func<InstrumentId, long?>? midPriceProvider = null)
    {
        _config = config;

        _sortedInstruments = instruments
            .OrderBy(i => i.DeliveryArea.Value, StringComparer.Ordinal)
            .ThenBy(i => i.DeliveryPeriod.Start)
            .ToArray();

        _instrumentIndex = new Dictionary<InstrumentId, int>(_sortedInstruments.Length);
        _instruments = new InstrumentState[_sortedInstruments.Length];

        for (var i = 0; i < _sortedInstruments.Length; i++)
        {
            var derivedSeed = unchecked(config.Seed ^ (int)((uint)i * 2654435761u));
            var rng = new Random(derivedSeed);

            double initialPrice;
            var mid = midPriceProvider?.Invoke(_sortedInstruments[i]);

            if (mid.HasValue)
            {
                initialPrice = mid.Value;
            }
            else
            {
                var jitter = (rng.NextDouble() * 0.30) - 0.15;
                initialPrice = config.DefaultSeedPriceTicks * (1.0 + jitter);
            }

            _instrumentIndex[_sortedInstruments[i]] = i;
            _instruments[i] = new InstrumentState(initialPrice, initialPrice, rng);
        }
    }

    /// <summary>
    /// Advances all instrument fair prices by one time step using geometric
    /// Brownian motion with externally-supplied (drift, vol) parameters.
    /// All instruments step under the same regime params on each call;
    /// the caller's regime schedule is the single source of regime state.
    /// </summary>
    public void StepAll(GbmParams regimeParams)
    {
        var dt = _config.Dt;
        var drift = regimeParams.Drift;
        var vol = regimeParams.Vol;

        for (var i = 0; i < _sortedInstruments.Length; i++)
        {
            ref var state = ref _instruments[i];

            var z = state.Rng.NextGaussian();
            var exponent = (drift - 0.5 * vol * vol) * dt + vol * Math.Sqrt(dt) * z;
            state.CurrentPrice *= Math.Exp(exponent);
        }
    }

    /// <summary>
    /// Returns the current simulated fair price for an instrument in ticks.
    /// </summary>
    public long GetFairPrice(InstrumentId instrument)
    {
        if (!_instrumentIndex.TryGetValue(instrument, out var index))
            throw new KeyNotFoundException($"Instrument not in model: {instrument}");

        return Math.Max(1L, (long)Math.Round(_instruments[index].CurrentPrice));
    }

    private struct InstrumentState
    {
        public double CurrentPrice;
        public readonly double SeedPrice;
        public readonly Random Rng;

        public InstrumentState(double currentPrice, double seedPrice, Random rng)
        {
            CurrentPrice = currentPrice;
            SeedPrice = seedPrice;
            Rng = rng;
        }
    }
}
