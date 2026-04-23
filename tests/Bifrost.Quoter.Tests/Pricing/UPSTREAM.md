# Upstream — tests/Bifrost.Quoter.Tests/Pricing/

## Files

| Donated path (this folder)   | Original path (Arena)                                                            | Arena commit SHA                           | Mutations applied                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
|------------------------------|----------------------------------------------------------------------------------|--------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `AvellanedaStoikovTests.cs`  | `tests/trader/ArenaTrader.Pricing.MeritOrder.Tests/AvellanedaStoikovTests.cs`    | `7983c32a97ca94dd02c89899ce4d1940ad625284` | Namespace `ArenaTrader.Pricing.MeritOrder.Tests` -> `Bifrost.Quoter.Tests.Pricing`. Removed `using ArenaTrader.Core;` (co-donated value types live in `Bifrost.Quoter.Pricing`). Replaced FluentAssertions assertions with plain xUnit `Assert.*` per BIFROST test convention. Dropped four `MeritOrderPricingConfig` / `AsParameters` tests whose dependencies are out of Phase 03 donation scope.                                                                                                                                                                                                                                                         |
| `GbmPriceModelTests.cs`      | `tests/trader/ArenaTrader.Core.Tests/Pricing/GbmPriceModelTests.cs`              | `9e11543a43a13a9bc4ea6a95e850df56f4d95520` | Namespace `ArenaTrader.Core.Tests.Pricing` -> `Bifrost.Quoter.Tests.Pricing`. `using ArenaTrader.Core.Pricing;` -> `using Bifrost.Quoter.Pricing;` + `using Bifrost.Exchange.Domain;`. Replaced FluentAssertions with plain xUnit `Assert.*` per BIFROST test convention. **D-09 adaptation**: every `StepAll()` call site rewired to `StepAll(new GbmParams(drift, vol))`. `MakeInstrument` helper rewritten to use BIFROST's `(DeliveryArea, DeliveryPeriod)` shape (Arena uses 4-arg `(DeliveryArea, start, end, PeriodType)`). `GbmConfig` constructor calls drop `BaseVolatility` + `RegimeTransitionRate` (those fields removed by the D-09 GbmConfig adaptation). 9 regime-coupled tests dropped per per-test triage rule with inline annotation. |
| `PyramidQuoteTrackerConcurrencyTests.cs` | `tests/trader/ArenaTrader.Strategies.MarketMaker.Tests/PyramidQuoteTrackerConcurrencyTests.cs` | `3d94185edc8b1e12f119740c98f197a3b38f513e` | Namespace `ArenaTrader.Strategies.MarketMaker.Tests` -> `Bifrost.Quoter.Tests.Pricing`. `using ArenaTrader.Core;` + `using ArenaTrader.Core.Events;` -> `using Bifrost.Exchange.Domain;` + `using Bifrost.Quoter.Pricing;` + `using OrderAccepted = Bifrost.Quoter.Pricing.Events.OrderAccepted;` (the using-alias resolves the same `OrderAccepted` name-clash described in `src/quoter/Pricing/PyramidQuoteTracker.UPSTREAM.md`; `CorrelationId` + `PyramidQuoteTracker` live in `Bifrost.Quoter.Pricing`; `InstrumentId`/`Side`/`OrderId`/`OrderType` live in `Bifrost.Exchange.Domain`). Replaced FluentAssertions with plain xUnit `Assert.*` (`readErrors.Should().Be(0)` -> `Assert.Equal(0, readErrors)`, `(working+pending+empty).Should().BeGreaterOrEqualTo(0)` -> `Assert.True(working+pending+empty >= 0)`, `readIterations.Should().BeGreaterThan(0)` -> `Assert.True(readIterations > 0)`). `MakeInstrument` rewritten to use BIFROST's `(DeliveryArea, DeliveryPeriod)` shape with one-hour periods (the original 1-hour offset semantics carry over byte-for-byte). All thread-loop / cancellation / counter logic byte-identical to Arena source. |

## Divergence rationale

### AvellanedaStoikovTests.cs — see Plan 03-01 (unchanged)

Two deliberate divergences from a strictly verbatim port:

1. **FluentAssertions -> plain xUnit `Assert`.** BIFROST test projects do not pin
   `FluentAssertions` (Phase 02 dropped Arena's pin in `Directory.Packages.props`).
   Each ported assertion was rewritten with the equivalent `Assert.*` shape:
   `Should().Be(x)` -> `Assert.Equal(x, actual)`,
   `Should().BeApproximately(x, t)` -> `Assert.Equal(x, actual, t)`,
   `Should().BeGreaterThan(x)` -> `Assert.True(actual > x)`,
   `Should().BeLessThan(x)` -> `Assert.True(actual < x)`,
   `Should().Be<Enum>` -> `Assert.Equal(<Enum>, actual)`.

2. **Dropped 4 config tests.** Arena's `Config_DefaultParams_CreatesSuccessfully`,
   `Config_EmptyPerArea_GetForAreaReturnsDefault`,
   `Config_PopulatedPerArea_GetForAreaReturnsConfiguredValue`,
   `Config_HittableMultiplierBelowOne_Throws` exercise `MeritOrderPricingConfig`,
   `AsParameters`, and `DeliveryArea` — none of which are in the Phase 03
   donation surface.

The 12 retained tests cover every public method on
`Bifrost.Quoter.Pricing.AvellanedaStoikov` and every co-donated value type
(`QuotableRange`, `HittableRange`, `SideBias`).

### GbmPriceModelTests.cs — Plan 03-02 (this file)

Three deliberate divergences:

1. **FluentAssertions -> plain xUnit `Assert`** — same convention as the sibling
   `AvellanedaStoikovTests.cs`. Notable mappings: `act.Should().Throw<Exception>()`
   -> `Assert.Throws<KeyNotFoundException>(act)` (specific exception type
   substituted because the production code throws `KeyNotFoundException`
   verbatim from the Arena source).

2. **`InstrumentId` shape adaptation**: BIFROST's `InstrumentId` is
   `(DeliveryArea, DeliveryPeriod)` whereas Arena's is
   `(DeliveryArea, DeliveryStart, DeliveryEnd, PeriodType)`. The `MakeInstrument`
   helper builds `new(new DeliveryArea(area), new DeliveryPeriod(start, end))`
   with one-hour periods (BIFROST's `DeliveryPeriod` constructor enforces
   exactly 15- or 60-minute durations, so all hourly tests use 60-min periods).
   `DeliveryArea.Parse(area)` -> `new DeliveryArea(area)` (BIFROST has no
   `.Parse` factory; the value-record's primary constructor is the only API).

3. **D-09 per-test triage**: 9 tests dropped because they touch Arena machinery
   removed by the Plan 03-02 `GbmPriceModel.cs` adaptation:
   - `RegimeDistribution_MeanRevertingIsMostFrequent` — calls `GetRegime` + asserts on `RegimeType`.
   - `AreaCorrelation_SameAreaSharesRegime_DifferentAreasMayDiffer` — calls `GetRegime`.
   - `Determinism_WithRegimes_SameSeedSameSequence` — calls `GetRegime`.
   - `Trending_ShowsPersistentDirectionalDrift` — filters by `RegimeType.Trending`.
   - `MeanReverting_PullsPriceTowardSeed` — filters by `RegimeType.MeanReverting`.
   - `Volatile_ShowsElevatedVariance` — filters by `RegimeType.Volatile`.
   - `GetRegime_KnownArea_ReturnsMeanReverting` — calls `GetRegime` (method removed).
   - `GetRegime_UnknownArea_Throws` — calls `GetRegime` (method removed).
   - `MidPrice_MeanReversionTargetsMidPrice` — depends on internal mean-reverting
     drift, which is now external regime-schedule responsibility (the model no
     longer pulls toward seed price; whatever drift the schedule supplies is what
     applies).

   Each drop is annotated in the test source with a single comment:
   `// DROPPED: Arena RegimeState/TryTransition not ported per Phase 03 D-09`.

The 14 retained tests cover the four named KEEP tests from the plan
(`Determinism_SameSeedAndInstruments`, `Determinism_DifferentSeeds`,
`Positivity_HighVolatility10000Steps`, `Independence_InstrumentAPricePath`)
plus 10 additional pure-math/pure-init tests that survive D-09 unchanged
(jitter behavior, getter contract, mid-price seeding, multi-instrument
independence, single-step advancement).

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- tests/trader/ArenaTrader.Pricing.MeritOrder.Tests/AvellanedaStoikovTests.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- tests/trader/ArenaTrader.Core.Tests/Pricing/GbmPriceModelTests.cs
```
