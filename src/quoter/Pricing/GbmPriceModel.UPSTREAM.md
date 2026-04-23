# Upstream — src/quoter/Pricing/ (GbmPriceModel + supporting files)

## Files

| Donated path (this folder) | Original path (Arena)                                            | Arena commit SHA                           | Mutations applied                                                                                                                                                                                                                                                                                                                                                                                                                                              |
|----------------------------|------------------------------------------------------------------|--------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GbmPriceModel.cs`         | `src/trader/ArenaTrader.Core/Pricing/GbmPriceModel.cs`           | `4c12f5f8596ce104af4f1a03065cdb0b21b152cf` | Namespace `ArenaTrader.Core.Pricing` -> `Bifrost.Quoter.Pricing`. `using ArenaTrader.Core;` -> `using Bifrost.Exchange.Domain;`. **D-09 strip**: deleted Arena fields `_areas`, `_areaInstrumentIndices`, `_transitionBuffer` (lines 11-14); deleted regime construction block (lines 53-74); replaced `StepAll()` body (lines 80-158) with `StepAll(GbmParams)` injecting external (drift, vol); deleted `GetRegime(DeliveryArea)` method (lines 171-177). Sort key `i.DeliveryStart` -> `i.DeliveryPeriod.Start` to match BIFROST `InstrumentId` shape `(DeliveryArea, DeliveryPeriod)`. Preserved verbatim: `_sortedInstruments` deterministic sort, Knuth-multiplier seed derivation, `InstrumentState` inner struct, exp-step formula, `Math.Max(1L, ...)` positivity floor, mid-price-or-jitter initialization. |
| `GbmConfig.cs`             | `src/trader/ArenaTrader.Core/Pricing/GbmConfig.cs`               | `4c12f5f8596ce104af4f1a03065cdb0b21b152cf` | Namespace `ArenaTrader.Core.Pricing` -> `Bifrost.Quoter.Pricing`. **D-09 adaptation**: removed fields `BaseVolatility` and `RegimeTransitionRate` (and their constructor parameters + validation guards). Both are now external concerns: per-tick volatility flows in via `GbmParams.Vol`; regime transitions are owned by the external `RegimeSchedule`. Retained: `DefaultSeedPriceTicks`, `Seed`, `Dt` with their positivity guards.                                                                                                                                                                                                                                                                                                                                       |
| `GbmParams.cs`             | (NEW — no Arena analog; authored in this plan)                   | n/a                                        | Fresh record `(double Drift, double Vol)` carrying per-tick regime parameters injected by external regime schedule (Plan 03-05). Replaces Arena's internal `RegimeState`/`RegimeType` machinery surgically.                                                                                                                                                                                                                                                                                                                                                                                       |
| `RandomExtensions.cs`      | `src/trader/ArenaTrader.Core/RandomExtensions.cs`                | `655581c7045ca107aef9a92778bb8c4368919a4e` | Namespace `ArenaTrader.Core` -> `Bifrost.Quoter.Pricing`. Co-donated because `GbmPriceModel.StepAll` consumes `Random.NextGaussian()` and the extension lives in Arena's `ArenaTrader.Core` root (not in `Pricing/`). Hosting it under the consumer's namespace avoids forcing a back-port into `Bifrost.Exchange.Domain` or a fresh `Bifrost.Common` library — same pattern Plan 03-01 used for `QuotableRange`/`HittableRange`/`SideBias`.                                                                                                                                                                                            |

## Divergence rationale

**D-09 (CONTEXT.md)**: BIFROST owns regime state externally in the `RegimeSchedule`
(authored in Plan 03-05). Arena's internal `RegimeState` / `RegimeType` /
`TryTransition` machinery in `GbmPriceModel.StepAll()` would silently fight the
external schedule (Landmine #1 in 03-CONTEXT.md). The strip is surgical:

- Deleted regime fields (`_areas`, `_areaInstrumentIndices`, `_transitionBuffer`),
  the regime-construction loop, the entire regime-switching `switch (regime.CurrentRegime)`
  block (MeanReverting / Trending / Volatile arms), and the `GetRegime(DeliveryArea)`
  accessor.
- Replaced the inner regime block with a flat per-instrument loop that pulls the
  exponent's `drift` and `vol` from the `GbmParams` argument.

Pure-math character is preserved: same Knuth-multiplier per-instrument seed
derivation (D-02 — load-bearing determinism constant), same `InstrumentState` struct,
same exponent formula `Math.Exp((drift - 0.5*vol*vol)*dt + vol*sqrt(dt)*z)`, same
`Math.Max(1L, ...)` positivity floor (Arena line 168), same `_sortedInstruments`
deterministic enumeration order (Arena lines 23-26 — load-bearing for Pitfall #7).

**InstrumentId shape**: BIFROST's `InstrumentId` is `(DeliveryArea, DeliveryPeriod)`
where `DeliveryPeriod` carries `Start` / `End`. Arena's is
`(DeliveryArea, DeliveryStart, DeliveryEnd, PeriodType)`. The deterministic-sort
ThenBy key adapted from `i.DeliveryStart` to `i.DeliveryPeriod.Start` accordingly.

**GbmConfig adaptation**: `BaseVolatility` and `RegimeTransitionRate` are dead
weight after D-09 (vol flows in via `GbmParams`; transition rate is the external
schedule's responsibility). Removing them keeps the config minimal and rejects
the implicit invariant that the model knows about volatility internally.

**RandomExtensions co-donation**: same pattern as Plan 03-01's co-donation of
`QuotableRange` / `HittableRange` / `SideBias` — when a donated Arena file depends
on a small utility not absorbed by Phase 02, co-donate it under the consumer's
namespace rather than back-porting into `Bifrost.Exchange.Domain` or a new
`Bifrost.Common` library.

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/Pricing/GbmPriceModel.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/Pricing/GbmConfig.cs
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/trader/ArenaTrader.Core/RandomExtensions.cs
```
