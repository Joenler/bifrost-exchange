# gRPC ↔ RabbitMQ DTO translation map

This document is the authoritative map from the **external gRPC surface**
(`bifrost-exchange/contracts/*.proto`) to the **internal RabbitMQ DTO surface**
(`bifrost-exchange/src/contracts-internal/Bifrost.Contracts.Internal/**`).

Every row here has (or will have, in the labeled phase) a `[Fact]` in
`tests/Bifrost.Contracts.Translation.Tests` asserting bit-equivalent
round-trip (`proto → DTO → proto`). Types whose DTO lands in a later phase
ship a minimal companion under `Bifrost.Contracts.Internal.Bifrost.*` in
the phase that introduces them.

## Boundary conventions (D-01, D-02)

Wire-boundary conversions the future-phase Gateway translator performs:

| gRPC side | DTO side | Notes |
|---|---|---|
| `int64 quantity_ticks` | `decimal Quantity` | via `QuantityScale.ToTicks` / `FromTicks` (interim `TicksPerUnit = 10_000`; scale finalized in a later phase) |
| `int64 price_ticks` (0 = absent) | `long? PriceTicks` (null = absent) | 0-is-absent convention on the wire side, nullable on the DTO side |
| `bifrost.market.v1.Side` enum (`SIDE_BUY`/`SIDE_SELL`) | `string Side` ("Buy" / "Sell") | translator string-switches at the boundary |
| `bifrost.market.v1.OrderType` enum | `string OrderType` ("Limit" / "Market" / "Iceberg" / "FillOrKill") | same |
| `int64 timestamp_ns` | `long TimestampNs` | 1:1 (no unit conversion) |
| `bifrost.market.v1.Instrument` message | `InstrumentIdDto(DeliveryArea, DeliveryPeriodStart, DeliveryPeriodEnd)` | `int64 delivery_period_*_ns` ↔ `DateTimeOffset` via `DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000)` (or nanosecond-accurate helper) |
| `RejectReason` enum | `string Reason` | translator maps `REJECT_REASON_X` → "X"-style case |

**`NanosecondStringConverter` stays JSON-internal.** It MUST NOT be referenced
from any file that also uses a `Bifrost.Contracts.<Surface>` gRPC type.
gRPC wire carries raw `int64 timestamp_ns`; DTO JSON serialisation uses the
string-encoded form for JavaScript-precision safety.

## Inbound commands (Client → Gateway → RabbitMQ)

| gRPC type | Internal DTO | Notes |
|---|---|---|
| `bifrost.strategy.v1.OrderSubmit` | `Bifrost.Contracts.Internal.Commands.SubmitOrderCommand` | D-01: int64_ticks ↔ decimal; enum Side/OrderType ↔ string; `display_slice_ticks` 0 ↔ null |
| `bifrost.strategy.v1.OrderCancel` | `Bifrost.Contracts.Internal.Commands.CancelOrderCommand` | InstrumentIdDto conversion per "Boundary conventions" |
| `bifrost.strategy.v1.OrderReplace` | `Bifrost.Contracts.Internal.Commands.ReplaceOrderCommand` | 0-is-absent on `new_price_ticks` / `new_quantity_ticks` → null on `NewPriceTicks` / `NewQuantity` |
| `bifrost.strategy.v1.Register` | — (gateway-only) | registration state lives in the Gateway's per-team map; no RabbitMQ DTO |
| `bifrost.strategy.v1.BidMatrixSubmit` | — (HTTP JSON to DAH) | routes via HTTP POST to DAH service, not through the RabbitMQ fabric (ARCHITECTURE.md §4) |

## Outbound events (RabbitMQ → Gateway → Client)

| gRPC type | Internal DTO | Notes |
|---|---|---|
| `bifrost.strategy.v1.MarketEvent.OrderAck` | `Bifrost.Contracts.Internal.Events.OrderAcceptedEvent` | `long OrderId` 1:1; `TimestampNs` 1:1 |
| `bifrost.strategy.v1.MarketEvent.OrderReject` | `Bifrost.Contracts.Internal.Events.OrderRejectedEvent` | enum `RejectReason` ↔ string `Reason` |
| `bifrost.strategy.v1.MarketEvent.Fill` | `Bifrost.Contracts.Internal.Events.OrderExecutedEvent` | `decimal Fee` ↔ `int64 fee_ticks`; `decimal FilledQuantity` ↔ `int64 filled_quantity_ticks`; `price_ticks`, `remaining_quantity_ticks` analogous |
| `bifrost.strategy.v1.MarketEvent.BookUpdate` | `Bifrost.Contracts.Internal.Events.BookDeltaEvent` | `BookLevel[] bids/asks` ↔ `BookLevelDto[] ChangedBids/ChangedAsks` |
| `bifrost.strategy.v1.MarketEvent.Trade` | `Bifrost.Contracts.Internal.Events.PublicTradeEvent` | `enum Side aggressor_side` ↔ `string AggressorSide`; `long TickSize` carried through |
| `bifrost.strategy.v1.MarketEvent.ForecastUpdate` | `Bifrost.Contracts.Internal.Events.ForecastUpdateEvent` | DTO carries `ForecastPriceTicks`, `HorizonNs`, `TimestampNs` (envelope-level on the proto side); no `ClientId` — public fairness invariant |
| `bifrost.strategy.v1.MarketEvent.ImbalancePrint` | `Bifrost.Contracts.Internal.Events.ImbalancePrintEvent` | Added v1.1.0 per D-12. 4 messages per Gate (one per quarter); `RoundNumber`, `InstrumentIdDto`, `QuarterIndex`, `PImbTicks`, `ATotalTicks`, `APhysicalTicks`, `Regime` enum ↔ string, `TimestampNs` inline on the proto |
| `bifrost.strategy.v1.MarketEvent.RoundState` | **BIFROST-specific — Phase 06** | DTO lands with the Round Orchestrator (wraps `bifrost.round.v1.RoundState`) |
| `bifrost.strategy.v1.MarketEvent.Scorecard` | **BIFROST-specific — Phase 10** | DTO lands with the Scoring loop |
| `bifrost.strategy.v1.MarketEvent.PositionSnapshot` | **BIFROST-specific — Phase 07** | DTO lands with the Gateway (position-authority rule GW-06) |
| `bifrost.strategy.v1.MarketEvent.RegisterAck` | — (gateway-only) | registration handshake; no RabbitMQ DTO |

## Public event payloads (MarketEvent.public_event wraps bifrost.events.v1.Event)

| gRPC type | Internal DTO | Notes |
|---|---|---|
| `bifrost.events.v1.Event.RegimeChange` | **BIFROST-specific — Phase 03** | DTO lands with the Quoter; `enum Regime` ↔ string |
| `bifrost.events.v1.Event.ForecastRevision` | `Bifrost.Contracts.Internal.Events.ForecastRevisionEvent` | `NewForecastPriceTicks`, `Reason`, `TimestampNs` (envelope-level); public, no team identity |
| `bifrost.events.v1.Event.News` | **BIFROST-specific — Phase 06b** | DTO lands with the MC Console |
| `bifrost.events.v1.Event.MarketAlert` | **BIFROST-specific — Phase 06b** | `enum Severity` ↔ string; DTO lands with the MC Console |
| `bifrost.events.v1.Event.ConfigChange` | **BIFROST-specific — Phase 06** | DTO lands with the Orchestrator |
| `bifrost.events.v1.Event.PhysicalShock` | `Bifrost.Contracts.Internal.Events.PhysicalShockEvent` | `int32 Mw`, `string Label`, `ShockPersistence` enum ↔ string, `int QuarterIndex` (optional `int32 quarter_index` landed in v1.1.0, required on the DTO — orchestrator enforces HasQuarterIndex at the boundary), `TimestampNs` envelope-level |

## Shared types (market.proto + round.proto + auction.proto)

| gRPC type | Internal DTO | Notes |
|---|---|---|
| `bifrost.market.v1.Instrument` | `Bifrost.Contracts.Internal.InstrumentIdDto` | `delivery_period_*_ns` ↔ `DateTimeOffset` — lossy below nanosecond resolution, intentional per D-01 |
| `bifrost.market.v1.BookView` | reassembled from `BookDeltaEvent` sequence | no 1:1 DTO; consumer reconstructs the book from deltas using `Sequence` monotonicity |
| `bifrost.market.v1.BookLevel` | `Bifrost.Contracts.Internal.Events.BookLevelDto` | `int64 quantity_ticks` ↔ `decimal Quantity`; `int32 order_count` 1:1 |
| `bifrost.market.v1.Side` | — (enum projected to string at every row that uses it) | translator is the single string-switch site |
| `bifrost.market.v1.OrderType` | — (enum projected to string at every row that uses it) | same |
| `bifrost.round.v1.RoundState` | **BIFROST-specific — Phase 06** | see `MarketEvent.RoundState` row above |
| `bifrost.auction.v1.BidStep` | `Bifrost.Contracts.Internal.Auction.BidStepDto` | `int64 price_ticks` 1:1 `long PriceTicks`; `int64 quantity_ticks` 1:1 `long QuantityTicks` (no QuantityScale at the auction boundary); round-trip covered by `AuctionBidStepTranslationTests` |
| `bifrost.auction.v1.BidMatrix` | `Bifrost.Contracts.Internal.Auction.BidMatrixDto` | `string team_name`, `string quarter_id`, `repeated BidStep buy_steps/sell_steps`; round-trip covered by `AuctionBidMatrixTranslationTests` |
| `bifrost.auction.v1.ClearingResult` | `Bifrost.Contracts.Internal.Auction.ClearingResultDto` | proto `string team_name = ""` (public summary) <-> DTO `string? TeamName = null`; positive `awarded_quantity_ticks` = net buy, negative = net sell; round-trip covered by `AuctionClearingResultTranslationTests` (summary + per-team) |

## MC surface (bidirectional — orchestrator gRPC)

The MC surface is an **internal gRPC** between the Round Orchestrator
(Phase 06) and the MC Console (Phase 06b); it does NOT cross the RabbitMQ
fabric. Listed here for completeness so readers don't assume "every gRPC
type has a RabbitMQ DTO".

| gRPC type | Internal DTO | Notes |
|---|---|---|
| `bifrost.mc.v1.McCommand` | — (internal gRPC only; no RabbitMQ side) | Phase 06 orchestrator is the single authority; command audit trail lands via a different path (MC-06) |
| `bifrost.mc.v1.McCommandResult` | — (internal gRPC only; no RabbitMQ side) | same |
| `bifrost.mc.v1.AuctionOpenCmd` / `AuctionCloseCmd` | — (internal gRPC only) | oneof variants of `McCommand.command`; auctions-phase transitions |
| `bifrost.mc.v1.RoundStartCmd` / `RoundEndCmd` / `GateCmd` / `SettleCmd` | — (internal gRPC only) | round-lifecycle transitions |
| `bifrost.mc.v1.NextRoundCmd` / `PauseCmd` / `ResumeCmd` / `AbortCmd` | — (internal gRPC only) | orchestrator flow control |
| `bifrost.mc.v1.ForecastReviseCmd` / `RegimeForceCmd` / `PhysicalShockCmd` | — (internal gRPC only) | MC-injected market impulses; event emission is the orchestrator's job |
| `bifrost.mc.v1.NewsFireCmd` / `NewsPublishCmd` / `AlertUrgentCmd` | — (internal gRPC only) | narrative injection → surfaces as `Event.News` / `Event.MarketAlert` via a different producer |
| `bifrost.mc.v1.TeamKickCmd` / `TeamResetCmd` | — (internal gRPC only) | gateway-scope side-effect; no message-bus mirror |
| `bifrost.mc.v1.ConfigSetCmd` | — (internal gRPC only) | emits `Event.ConfigChange` downstream via orchestrator |
| `bifrost.mc.v1.LeaderboardRevealCmd` / `EventEndCmd` | — (internal gRPC only) | terminal MC signals |

## Imbalance-simulator private events (internal-only; no gRPC counterpart)

Per-team settlement rows are delivered over the internal RabbitMQ fabric on
each team's private queue and are never projected onto the team-facing gRPC
`MarketEvent` oneof. Teams consume the row directly from their private queue
binding on `private.imbalance.settlement.<clientId>`.

| gRPC type | Internal DTO | Notes |
|---|---|---|
| — (no gRPC analog — internal-only per D-14) | `Bifrost.Contracts.Internal.Events.ImbalanceSettlementEvent` | Private per-(team, quarter) row at RoundState=Settled. `RoundNumber`, `ClientId`, `InstrumentIdDto`, `QuarterIndex`, `PositionTicks`, `PImbTicks`, `ImbalancePnlTicks` (= `PositionTicks * PImbTicks`), `TimestampNs`. Teams cross-check against the public `ImbalancePrint` for the same quarter. |

## MC regime-force translation (orchestrator → quoter)

The MC `RegimeForceCmd` variant is the one MC command whose effect crosses
the internal RabbitMQ fabric (the orchestrator translates it; the quoter
consumes it). Listed separately from the MC surface table because both ends
exist today.

| gRPC (mc.proto) | Internal JSON DTO | Subscriber |
|---|---|---|
| `bifrost.mc.v1.McCommand.regime_force` carrying `RegimeForceCmd { regime: Regime, nonce: string (Guid) }` | `Bifrost.Quoter.Rabbit.McRegimeForceDto { Regime, Nonce: Guid }` published on exchange `bifrost.mc` with routing key `mc.regime.force` (queue `bifrost.mc.regime`) | `Bifrost.Quoter.Rabbit.McRegimeForceConsumer` (poll-mode `BackgroundService`) deserializes and forwards a `RegimeForceMessage` into the shared `Channel<RegimeForceMessage>` inbox; the quoter's `RegimeSchedule.InstallMcForce` enforces nonce idempotency via an `LruSet` so RabbitMQ at-least-once redeliveries are dropped. |

The orchestrator (Phase 06) is responsible for issuing a fresh `Guid` nonce
per force issuance. The quoter is the sole emitter of public
`Event.RegimeChange` (routing key `events.regime.change` on
`bifrost.public`); the orchestrator does NOT emit the public event itself.

## Envelope pattern (D-03)

All RabbitMQ DTOs are wrapped in `Envelope<T>(MessageType, TimestampUtc,
CorrelationId?, ClientId?, InstrumentId?, Sequence?, T Payload)`. The
future-phase Gateway translator:

- **On ingress (gRPC → RabbitMQ):** builds an `Envelope<T>` around the
  translated DTO; `MessageType` comes from `MessageTypes.<Constant>`.
- **On egress (RabbitMQ → gRPC):** strips the envelope, translates the
  payload, and emits the gRPC message on the per-team bidi stream.

`Envelope<T>` itself is **not** on the gRPC wire — gRPC uses native
`oneof` discrimination on `StrategyCommand` and `MarketEvent` for the
same job.

## Coverage assertion (CONT-07 hook)

The translation test project ships one `[Fact]` per row in the "Inbound
commands" and "Outbound events" sections above whose DTO exists today (8
concrete, today-actionable rows; the BIFROST-specific rows land in their
labeled phases). The test project's CI slot fails if any of these 8 rows
lacks a corresponding `[Fact]`; this is the machine-checkable
CONT-06-to-CONT-07 coverage link.

## Historical note

Arena's `NanosecondStringConverter` JSON shape was the source of three
wire-drift bugs (`v3.1` nanosecond-vs-tick — see Arena `CLAUDE.md`). BIFROST
structurally avoids the class by keeping the converter JSON-internal and
using raw `int64 timestamp_ns` on the gRPC wire (D-02).
