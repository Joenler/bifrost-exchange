# Upstream — src/exchange/Exchange.Application/

Arena commit SHA: `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`
Arena source root: `src/exchange/Exchange.Application/`
Copied: 2026-04-23

Note on SHA: this per-folder `UPSTREAM.md` records the CURRENT Arena SHA at port time per
the fork-as-of-port semantics shared with `src/exchange/Exchange.Domain/UPSTREAM.md`. The
repo-root `UPSTREAM.md` anchor stays historically accurate for Bifrost.Time (Phase 00).
The original planning docs referenced `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`; the
Arena history advanced between planning and port, and 8419a0c was the HEAD of the Arena
`Exchange.Application/` folder when these files were copied.

## KEEP (14 files ported, namespace + clock + telemetry mutations only)

| Donated path (this folder)      | Original path (Arena)                                              | Mutations |
| ------------------------------- | ------------------------------------------------------------------ | --------- |
| `OrderValidator.cs`             | `src/exchange/Exchange.Application/OrderValidator.cs`              | Namespace rewrite; `using Bifrost.Time;` added; `clock.UtcNow` → `clock.GetUtcNow()` (1 site, line 25). NOTE: RoundState gate guard + `IRoundStateSource` parameter are intentionally deferred to a later plan. |
| `OrderValidationResult.cs`      | `src/exchange/Exchange.Application/OrderValidationResult.cs`       | Namespace rewrite only. |
| `ExchangeService.cs`            | `src/exchange/Exchange.Application/ExchangeService.cs`             | Namespace rewrite; `using Bifrost.Time;` added; `using System.Diagnostics;` dropped (Stopwatch + TagList only used by telemetry); 3 ctor params dropped (`OrderStatsCollector stats`, `ExchangeThroughputTracker? throughputTracker = null`, `ExchangeMetrics? metrics = null`); all `stats.Record*` / `throughputTracker?.Record*` / `metrics?.*` call sites removed (13+ sites); Stopwatch-based MatchingLatency measurement removed (was metrics-only); `clock.UtcNow` → `clock.GetUtcNow()` (5 sites: lines 25, 176, 241, 324, 347 in the original). `RefreshInstruments` method removed (Arena used it with `InstrumentRefreshWorker` which is dropped per CONTEXT.md D-05; TradingCalendar.GenerateInstruments() is now no-arg and Phase 06 orchestrator will replace rotation). `FlushAndPublishOrderStats` method removed (depended on dropped `stats`). |
| `BookPublisher.cs`              | `src/exchange/Exchange.Application/BookPublisher.cs`               | Namespace rewrite; `using System.Diagnostics;` dropped; `ExchangeMetrics? metrics = null` ctor param dropped; `metrics?.BookMessagesPublished.Add(...)` call site removed. |
| `BookDeltaBuilder.cs`           | `src/exchange/Exchange.Application/BookDeltaBuilder.cs`            | Namespace rewrite only. |
| `BookSnapshotBuilder.cs`        | `src/exchange/Exchange.Application/BookSnapshotBuilder.cs`         | Namespace rewrite only. |
| `TradePublisher.cs`             | `src/exchange/Exchange.Application/TradePublisher.cs`              | Namespace rewrite; `ExchangeThroughputTracker? throughputTracker` ctor param dropped; `throughputTracker?.RecordTrade()` call site removed. |
| `InstrumentRegistry.cs`         | `src/exchange/Exchange.Application/InstrumentRegistry.cs`          | Namespace rewrite only. Non-compound ConcurrentDictionary (`TryAdd`, `GetValueOrDefault`) — passes `lint-concurrent-dictionary.sh`. |
| `PublicSequenceTracker.cs`      | `src/exchange/Exchange.Application/PublicSequenceTracker.cs`       | Namespace rewrite only. Plain Dictionary — single-consumer writes. |
| `TimestampHelper.cs`            | `src/exchange/Exchange.Application/TimestampHelper.cs`             | Namespace rewrite only. Pure static math. |
| `InstrumentIdMapping.cs`        | `src/exchange/Exchange.Application/InstrumentIdMapping.cs`         | Namespace rewrite; `using Contracts;` → `using Bifrost.Contracts.Internal;`; `using Exchange.Domain;` → `using Bifrost.Exchange.Domain;`. |
| `ExchangeRulesConfig.cs`        | `src/exchange/Exchange.Application/ExchangeRulesConfig.cs`         | Namespace rewrite only. |
| `IEventPublisher.cs`            | `src/exchange/Exchange.Application/IEventPublisher.cs`             | Namespace rewrite only. |
| `TradingCalendar.cs`            | `src/exchange/Exchange.Application/TradingCalendar.cs`             | ADAPTED: Arena's rolling-window 240-instrument generator replaced with a static 5-instrument DE-only registry on synthetic far-future date `9999-01-01T00:00Z` (1 hour + 4 quarters). `GenerateInstruments()` is now no-arg. `DeliveryAreas = ["DE1", "FR"]` removed. `FloorToHour` / `FloorToQuarterHour` helpers removed. Phase 06 orchestrator will replace this when real round calendars exist. |

## DROP (5 files intentionally NOT copied)

| Arena file                          | Rationale |
| ----------------------------------- | --------- |
| `IClock.cs`                         | Superseded by `Bifrost.Time.IClock` (GetUtcNow() method — diverges from Arena's UtcNow property to match `TimeProvider` shape and keep BannedApiAnalyzers' error message truthful). |
| `SystemClock.cs`                    | Superseded by `Bifrost.Time.SystemClock`. |
| `ExchangeMetrics.cs`                | Arena trader-dashboard telemetry (OpenTelemetry counters / histograms). BIFROST bigscreen consumes public RabbitMQ feeds directly per Phase 10 (CONTEXT.md D-02). |
| `ExchangeThroughputTracker.cs`      | Arena throughput-per-instrument counters (Interlocked ops). Not in BIFROST's scope — the recorder owns per-event auditing via SQLite (REC-02). |
| `OrderStatsCollector.cs`            | Arena stats-aggregation bridge that forwards derived metrics to a public RabbitMQ topic. Not in BIFROST. |

## Divergences summary

- **Clock rewire sites:** 5 in `ExchangeService.cs` (lines 25, 176, 241, 324, 347 in the original) + 1 in `OrderValidator.cs` (line 25 in the original). All route through `Bifrost.Time.IClock.GetUtcNow()`. Acceptance gate: `rg 'DateTime(Offset)?\.UtcNow|clock\.UtcNow' src/exchange/Exchange.Application/` returns zero hits.
- **Telemetry drops:** 3 ctor parameters removed across `ExchangeService`, `BookPublisher`, `TradePublisher`. Approximately 13 telemetry call sites removed across `ExchangeService`. Stopwatch-based matching-latency measurement removed in `ExchangeService` (was metrics-only).
- **TradingCalendar shape change:** Arena's rolling-window 240-instrument generator (24 hours × 2 areas + 96 quarters × 2 areas) replaced with a static 5-instrument DE-only registry (1 hour + 4 quarters) on synthetic far-future date `9999-01-01T00:00Z`. The synthetic date keeps `DeliveryPeriod.HasExpired` (which now correctly fires at delivery Start per the BIFROST intraday-power semantic, fix 7f31dd3) false regardless of clock during tests.
- **Namespace mass-rewrite:** `namespace Exchange.Application;` → `namespace Bifrost.Exchange.Application;`. `using Exchange.Domain;` → `using Bifrost.Exchange.Domain;`. `using Contracts;` / `using Contracts.Commands;` / `using Contracts.Events;` → `using Bifrost.Contracts.Internal;` / `using Bifrost.Contracts.Internal.Commands;` / `using Bifrost.Contracts.Internal.Events;`.
- **Methods removed (dead after drops):** `ExchangeService.RefreshInstruments(DateTimeOffset, Func<...>)` — Arena's `InstrumentRefreshWorker` is dropped per CONTEXT.md D-05 and `TradingCalendar.GenerateInstruments` is no-arg in BIFROST. `ExchangeService.FlushAndPublishOrderStats()` — depended on dropped `OrderStatsCollector`.

## Commit SHA lookup

```bash
cd /Users/jonathanjonler/RiderProjects/Arena
git rev-parse HEAD   # confirm Arena has not advanced since the date above
```

Re-run at port time; overwrite the SHA above if Arena has advanced since the donation date.
