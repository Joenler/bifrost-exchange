using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Pricing;
using Xunit;

namespace Bifrost.Quoter.Tests.Pricing;

/// <summary>
/// Pure-math tests for GbmPriceModel. See UPSTREAM.md for Arena provenance,
/// the assertion-style adaptation (FluentAssertions -> plain xUnit Assert),
/// and the per-test triage drop list (regime-coupled tests dropped per D-09).
/// StepAll() call sites pass `new GbmParams(drift, vol)` per call now that
/// regime state is owned externally.
/// </summary>
public class GbmPriceModelTests
{
    private static readonly DateTimeOffset BaseStart =
        new(2026, 3, 5, 10, 0, 0, TimeSpan.Zero);

    private static GbmConfig DefaultConfig(int seed = 42) =>
        new(DefaultSeedPriceTicks: 5000, Seed: seed);

    // Default per-tick regime params used by tests that previously relied on
    // Arena's internal volatility config. (drift = 0.0, vol = 0.3) mirrors
    // Arena's old DefaultConfig(volatility: 0.3) starting point.
    private static GbmParams DefaultParams(double vol = 0.3) =>
        new(Drift: 0.0, Vol: vol);

    private static InstrumentId MakeInstrument(string area, int hourOffset = 0) =>
        new(
            new DeliveryArea(area),
            new DeliveryPeriod(
                BaseStart.AddHours(hourOffset),
                BaseStart.AddHours(hourOffset + 1)));

    [Fact]
    public void Determinism_SameSeedAndInstruments_ProducesIdenticalPrices()
    {
        var instruments = new[] { MakeInstrument("DE1"), MakeInstrument("DE1", 1) };
        var config = DefaultConfig(seed: 123);
        var regimeParams = DefaultParams();

        var model1 = new GbmPriceModel(config, instruments);
        var model2 = new GbmPriceModel(config, instruments);

        for (var step = 0; step < 100; step++)
        {
            model1.StepAll(regimeParams);
            model2.StepAll(regimeParams);

            foreach (var inst in instruments)
            {
                Assert.Equal(model2.GetFairPrice(inst), model1.GetFairPrice(inst));
            }
        }
    }

    [Fact]
    public void Determinism_DifferentSeeds_ProducesDifferentPrices()
    {
        var instruments = new[] { MakeInstrument("DE1") };
        var regimeParams = DefaultParams();
        var model1 = new GbmPriceModel(DefaultConfig(seed: 1), instruments);
        var model2 = new GbmPriceModel(DefaultConfig(seed: 2), instruments);

        for (var step = 0; step < 10; step++)
        {
            model1.StepAll(regimeParams);
            model2.StepAll(regimeParams);
        }

        Assert.NotEqual(model2.GetFairPrice(instruments[0]), model1.GetFairPrice(instruments[0]));
    }

    [Fact]
    public void Positivity_HighVolatility10000Steps_AllPricesPositive()
    {
        var instruments = new[] { MakeInstrument("DE1"), MakeInstrument("FR") };
        var config = DefaultConfig(seed: 77);
        var regimeParams = DefaultParams(vol: 1.0);
        var model = new GbmPriceModel(config, instruments);

        for (var step = 0; step < 10_000; step++)
        {
            model.StepAll(regimeParams);

            foreach (var inst in instruments)
            {
                Assert.True(model.GetFairPrice(inst) > 0,
                    $"price must be positive at step {step} for {inst}");
            }
        }
    }

    [Fact]
    public void Independence_InstrumentAPricePath_UnchangedByPresenceOfB()
    {
        var instrumentA = MakeInstrument("AT");
        var instrumentB = MakeInstrument("DE1");
        var regimeParams = DefaultParams();

        var configAB = DefaultConfig(seed: 99);
        var configA = DefaultConfig(seed: 99);

        var modelAB = new GbmPriceModel(configAB, new[] { instrumentA, instrumentB });
        var modelA = new GbmPriceModel(configA, new[] { instrumentA });

        for (var step = 0; step < 50; step++)
        {
            modelAB.StepAll(regimeParams);
            modelA.StepAll(regimeParams);

            Assert.Equal(modelA.GetFairPrice(instrumentA), modelAB.GetFairPrice(instrumentA));
        }
    }

    [Fact]
    public void Jitter_TwoInstrumentsInSameArea_DifferentInitialPrices()
    {
        var inst1 = MakeInstrument("DE1", 0);
        var inst2 = MakeInstrument("DE1", 1);
        var config = DefaultConfig(seed: 42);
        var model = new GbmPriceModel(config, new[] { inst1, inst2 });

        var price1 = model.GetFairPrice(inst1);
        var price2 = model.GetFairPrice(inst2);

        Assert.NotEqual(price2, price1);
    }

    [Fact]
    public void Jitter_InitialPrice_WithinFifteenPercentOfDefault()
    {
        var inst = MakeInstrument("DE1");
        var config = DefaultConfig(seed: 42);
        var model = new GbmPriceModel(config, new[] { inst });

        var price = model.GetFairPrice(inst);
        var defaultTicks = config.DefaultSeedPriceTicks;
        var lowerBound = (long)(defaultTicks * 0.85);
        var upperBound = (long)(defaultTicks * 1.15);

        Assert.True(price >= lowerBound && price <= upperBound,
            $"initial price {price} should be within +/-15% of default {defaultTicks}");
    }

    [Fact]
    public void GetFairPrice_ReturnsLongTicks()
    {
        var inst = MakeInstrument("DE1");
        var config = DefaultConfig();
        var model = new GbmPriceModel(config, new[] { inst });

        var price = model.GetFairPrice(inst);

        Assert.True(price > 0);
    }

    [Fact]
    public void GetFairPrice_UnknownInstrument_Throws()
    {
        var known = MakeInstrument("DE1");
        var unknown = MakeInstrument("FR");
        var config = DefaultConfig();
        var model = new GbmPriceModel(config, new[] { known });

        Assert.Throws<KeyNotFoundException>(() => model.GetFairPrice(unknown));
    }

    [Fact]
    public void StepAll_AdvancesPrices()
    {
        var inst = MakeInstrument("DE1");
        var config = DefaultConfig(seed: 42);
        var model = new GbmPriceModel(config, new[] { inst });

        var priceBefore = model.GetFairPrice(inst);
        model.StepAll(new GbmParams(Drift: 0.0, Vol: 0.3));
        var priceAfter = model.GetFairPrice(inst);

        Assert.NotEqual(priceBefore, priceAfter);
    }

    [Fact]
    public void Independence_ThreeInstrumentsAcrossAreas_PricesCorrect()
    {
        var de1 = MakeInstrument("DE1");
        var fr = MakeInstrument("FR");
        var nl = MakeInstrument("NL");

        var config = DefaultConfig(seed: 7);
        var regimeParams = DefaultParams();
        var model = new GbmPriceModel(config, new[] { de1, fr, nl });

        for (var step = 0; step < 50; step++)
            model.StepAll(regimeParams);

        var priceDe1 = model.GetFairPrice(de1);
        var priceFr = model.GetFairPrice(fr);
        var priceNl = model.GetFairPrice(nl);

        Assert.True(priceDe1 > 0);
        Assert.True(priceFr > 0);
        Assert.True(priceNl > 0);

        var prices = new[] { priceDe1, priceFr, priceNl };
        Assert.Equal(3, prices.Distinct().Count());
    }

    // DROPPED: Arena RegimeState/TryTransition not ported per Phase 03 D-09
    // - RegimeDistribution_MeanRevertingIsMostFrequent (asserts on GetRegime + RegimeType)
    // - AreaCorrelation_SameAreaSharesRegime_DifferentAreasMayDiffer (asserts on GetRegime)
    // - Determinism_WithRegimes_SameSeedSameSequence (asserts on GetRegime)
    // - Trending_ShowsPersistentDirectionalDrift (filters by RegimeType.Trending)
    // - MeanReverting_PullsPriceTowardSeed (filters by RegimeType.MeanReverting)
    // - Volatile_ShowsElevatedVariance (filters by RegimeType.Volatile)
    // - GetRegime_KnownArea_ReturnsMeanReverting (calls GetRegime — method removed)
    // - GetRegime_UnknownArea_Throws (calls GetRegime — method removed)
    // - MidPrice_MeanReversionTargetsMidPrice (depends on internal mean-reverting drift,
    //   which is now an external regime-schedule concern; behavior no longer in this model)

    // --- Mid-price seeding tests (kept — initialization is pure-math, no regime coupling) ---

    [Fact]
    public void MidPrice_ExactValueUsed_NoJitter()
    {
        var inst = MakeInstrument("DE1");
        var config = DefaultConfig(seed: 42);

        Func<InstrumentId, long?> provider = _ => 6000L;
        var model = new GbmPriceModel(config, new[] { inst }, midPriceProvider: provider);

        Assert.Equal(6000L, model.GetFairPrice(inst));
    }

    [Fact]
    public void MidPrice_NullFallsBackToDefaultPlusJitter()
    {
        var inst = MakeInstrument("DE1");
        var config = DefaultConfig(seed: 42);

        Func<InstrumentId, long?> provider = _ => null;
        var model = new GbmPriceModel(config, new[] { inst }, midPriceProvider: provider);

        var price = model.GetFairPrice(inst);
        var lower = (long)(config.DefaultSeedPriceTicks * 0.85);
        var upper = (long)(config.DefaultSeedPriceTicks * 1.15);

        Assert.True(price >= lower && price <= upper,
            $"null mid-price should fall back to default + jitter; got {price}");
    }

    [Fact]
    public void MidPrice_MixedSeeding_MidPriceAndDefault()
    {
        var instA = MakeInstrument("AT");
        var instB = MakeInstrument("DE1");
        var config = DefaultConfig(seed: 42);

        Func<InstrumentId, long?> provider = id =>
            id.DeliveryArea.Value == "AT" ? 6000L : null;

        var model = new GbmPriceModel(config, new[] { instA, instB }, midPriceProvider: provider);

        Assert.Equal(6000L, model.GetFairPrice(instA));

        var priceB = model.GetFairPrice(instB);
        var lower = (long)(config.DefaultSeedPriceTicks * 0.85);
        var upper = (long)(config.DefaultSeedPriceTicks * 1.15);
        Assert.True(priceB >= lower && priceB <= upper,
            $"instrument B without mid-price should use default + jitter; got {priceB}");
    }

    [Fact]
    public void MidPrice_NullProvider_IdenticalToTwoArgConstructor()
    {
        var instruments = new[] { MakeInstrument("DE1"), MakeInstrument("DE1", 1) };
        var config = DefaultConfig(seed: 42);
        var regimeParams = DefaultParams();

        var model2Arg = new GbmPriceModel(config, instruments);
        var model3Arg = new GbmPriceModel(config, instruments, midPriceProvider: null);

        foreach (var inst in instruments)
        {
            Assert.Equal(model2Arg.GetFairPrice(inst), model3Arg.GetFairPrice(inst));
        }

        for (var step = 0; step < 50; step++)
        {
            model2Arg.StepAll(regimeParams);
            model3Arg.StepAll(regimeParams);

            foreach (var inst in instruments)
            {
                Assert.Equal(model2Arg.GetFairPrice(inst), model3Arg.GetFairPrice(inst));
            }
        }
    }
}
