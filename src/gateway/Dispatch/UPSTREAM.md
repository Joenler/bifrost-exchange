# Bifrost.Gateway.Dispatch — UPSTREAM

Donated from: Arena/src/trader/ArenaTrader.Host/ForecastDispatcher.cs
Arena commit SHA: ee1b0cc0479d77e846585e48b3ea20db0fdba534
Donation date: 2026-04-25
Reason: Five-mechanism cohort-jittered dispatcher; canonical Arena implementation reused for GW-08.

## Surgical rewrites (the only changes to Arena's source)

1. **Namespace**: `ArenaTrader.Host` → `Bifrost.Gateway.Dispatch`. Arena `using` directives
   (`Arena.Simulation`, `ArenaTrader.Core`, `ArenaTrader.Pricing.MeritOrder`,
   `ArenaTrader.Risk`, `Contracts.Events`) replaced with BIFROST equivalents
   (`Bifrost.Contracts.Internal`, `Bifrost.Contracts.Internal.Events`,
   `Bifrost.Exchange.Infrastructure.RabbitMq`, `Bifrost.Gateway.State`,
   `Bifrost.Time`, `Bifrost.Contracts.Strategy`).

2. **`Random.Shared` → seeded `_jitterRng`**: every `Random.Shared.NextDouble()` site
   replaced with `_jitterRng.NextDouble()`. `_jitterRng` is constructed in the ctor as
   `new Random((int)((clock.GetUtcNow().UtcTicks ^ Environment.ProcessId) & 0xFFFFFFFF))`.
   Three jitter sites in the body: start jitter, inter-tick jitter (symmetric around
   baseline), intra-cohort spread. `BannedSymbols.txt` (P:System.Random.Shared) catches
   any miss at compile time. The internal `SetJitterRngForTest(Random)` seam lets unit
   tests pin determinism with a fixed seed.

3. **Member set**: `IReadOnlyList<StrategyRuntimeEntry> registrations` →
   `TeamRegistry _registry`. Arena holds a static list of strategies at construction;
   BIFROST's set is dynamic (teams join via `StrategyGatewayService.StreamStrategy`'s
   first-frame Register handshake). `DispatchOneTickAsync` now snapshots
   `_registry.SnapshotAll()` at the top of each tick and filters by cohort assignment
   via `CohortAssignment.CohortFor(team.TeamName, _cohortCount) == _currentCohort`.
   The `IForecastArrivalAware` interface check is gone — every TeamState is a candidate.

4. **Per-member action**: `aware.OnForecastArrival(dispatchNow, isNwpRevision)` →
   ring-Append + `team.Outbound.WriteAsync(marketEvent, ct)` under the
   Pitfall 10 lock-release-before-write contract:
   - `lock (team.StateLock) { team.Ring.Append(envelope); }` (so reconnect-replay
     sees the ForecastUpdate).
   - Release lock; `if (team.Outbound is { } writer) await writer.WriteAsync(marketEvent, ct);`
   - `OperationCanceledException` on the writer is swallowed and the dispatcher continues
     with the next cohort member (a single team disconnecting mid-tick must not
     skip the rest of the cohort).

## Members removed (Arena-only; not BIFROST surface)

- `IntraCohortOuOverlay` field + `Step()` call (Arena lines ~186-192) — Arena-only
  Ornstein-Uhlenbeck overlay concept; BIFROST's forecast is the scalar from Phase 04.
- `_metricsPublisher.PublishForecastSnapshotAsync(snapshot, ct)` (Arena lines ~247-251)
  — replaced by `GatewayMetrics.ForecastsDispatched.Inc(team.TeamName)` once Plan 08
  lands metrics. Plan 07-07 leaves a TODO at the call site.
- `IForecastArrivalAware` interface check — replaced by direct write to `team.Outbound`.
  Every team is a candidate dispatchee in BIFROST.
- `BuildForecastSnapshot` + `Catalog` + `StrategyForecastSnapshot` — Arena's metrics
  payload shape; BIFROST's metrics will live in `gateway_forecasts_dispatched_total`
  counters in Plan 08, not a structured snapshot DTO.
- `ForecastEngine` + `ForecastEngineTicker` + `_lastNwpRevisionTime` + `isNwpRevision`
  flag — Arena fires NWP revisions on a separate calendar interval. BIFROST has no NWP
  revisions; the public bus delivers a single `ForecastUpdate` event class. The dispatcher
  reads the latest snapshot from a `volatile ForecastSnapshot? _latestForecast` updated
  by an internal AsyncEventingBasicConsumer bound to `bifrost.public/public.forecast`.

## GSD markers stripped

- (none — Arena does not embed phase numbers in this file).

## Forecast source

Arena reads from `ForecastEngine`. BIFROST consumes RabbitMQ
`bifrost.public` exchange with binding key `public.forecast` (Phase 04 IMB-01) via a
private `AsyncEventingBasicConsumer` that the `ForecastDispatcher` constructs in its
own `ExecuteAsync`. The consumer updates a `volatile ForecastSnapshot? _latestForecast`;
the cohort dispatch loop reads that snapshot and dispatches to cohort members.
The bound channel is dedicated to the dispatcher (Pitfall 6); it is NOT shared with
`PublicEventConsumer` (which deliberately omits `public.forecast` binding per Plan 06).

## Lifecycle

Arena exposes manual `Start(CancellationToken)` + `StopAsync()`. BIFROST inherits from
`Microsoft.Extensions.Hosting.BackgroundService` and uses `ExecuteAsync` so the host
container manages the lifecycle deterministically — same shape as Plan 06's four
RabbitMQ consumers. `IHostApplicationLifetime.ApplicationStopping` cancels the
`stoppingToken` cleanly; channel close + dispose happen in `StopAsync`.
