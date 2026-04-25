# Bifrost.Gateway.Dispatch — README

This package owns BIFROST's cohort-jittered dispatch for `ForecastUpdate` per GW-08.

## Why is jitter here intentional?

Synchronized "pulse" failures killed Arena's quoter on regime transitions. When N teams
all receive identical-timestamp `ForecastUpdate` events and react simultaneously, the
exchange sees a thundering herd of orders within ~1 ms — order book depth craters,
spreads explode, and the quoter's microprice goes haywire. Arena solved this with
five mechanisms (cohort assignment + round-robin + start jitter + intra-cohort spread
+ inter-tick jitter). BIFROST inherits that solution by porting the dispatcher
verbatim with EXACTLY 4 surgical rewrites (see `UPSTREAM.md`).

## Five jitter mechanisms

1. **Cohort assignment** (`CohortAssignment.CohortFor`): each team is hashed (FNV-1a,
   stable across reconnects + .NET runtime versions) into one of `CohortCount`
   buckets. Teams in different cohorts receive `ForecastUpdate` on different ticks.
2. **Round-robin**: `_currentCohort = (_currentCohort + 1) % _cohortCount` so each
   cohort fires once per `cohortCount * cohortInterval` window.
3. **Start jitter**: a one-time delay drawn from `[0, _cohortStartJitter]` desynchronizes
   the dispatch clock from wall-clock boundaries.
4. **Intra-cohort spread**: per-member delay drawn from `[0, _intraCohortDispatchSpread]`
   so even within one cohort, members don't all fire at the same wall-clock instant.
5. **Inter-tick jitter**: symmetric per-tick variation around `_cohortInterval`
   (`[-jitter/2, +jitter/2]`) so the cohort phase drifts instead of locking onto a
   fixed wall-clock phase forever.

## What gets jittered

`ForecastUpdate` only (CONTEXT D-02). Every other outbound event class
(BookUpdate / Trade / RegimeChange / News / MarketAlert / RoundState / Scorecard /
OrderAck / OrderReject / Fill / PositionSnapshot / RegisterAck / ClearingResult) fans
out in arrival order with no intentional per-team delay (Plan 06 owns those consumers).

## What gets configured

`appsettings.json` carries the knobs under `Gateway:ForecastDispatch:*`:

- `CohortCount` (default 3 — Arena default; D-16)
- `CohortIntervalMs` (default 15 000 — matches Phase 04 IMB-01 forecast cadence)
- `CohortStartJitterMs` (default 1 000)
- `IntraCohortDispatchSpreadMs` (default 500)
- `InterTickJitterMs` (default 200)

## What MUST NOT be done

- **DO NOT zero the jitter knobs to "fix" determinism.** GW-08 explicitly names this as
  intentional non-determinism. Determinism in tests comes from injecting a seeded
  `Random` (via `ForecastDispatcher.SetJitterRngForTest`) — NOT from setting jitter to 0.
- **Do NOT measure end-to-end RabbitMQ-publish-complete → wire latency for ForecastUpdate
  against the GW-09 p99 < 100 ms SLO.** RESEARCH Pitfall 4 is explicit: the SLO measures
  "dispatcher decides to emit → wire" — the cohort jitter delay is BEFORE the dispatcher
  decides. Phase 12 dry-run will see 15 s + cohort_position × 5 s + jitter end-to-end for
  ForecastUpdate; that is by design.
- **Do NOT bind `public.forecast` in `PublicEventConsumer`.** Plan 06 deliberately omits
  that binding so the cohort-jittered fan-out lives only here.

## See also

- ADR-0002 (gateway architecture)
- 07-RESEARCH.md §Code Examples §Pattern 5 (port instructions)
- 07-CONTEXT.md D-15..D-17 (cohort assignment + count + jitter magnitudes)
- ./UPSTREAM.md (Arena donor SHA + 4 rewrites)
