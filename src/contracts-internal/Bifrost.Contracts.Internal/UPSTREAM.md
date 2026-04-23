# UPSTREAM

**Source repo:** Arena (`/Users/jonathanjonler/RiderProjects/Arena/`)
**Source commit:** `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`
**Source date:** 2026-04-22
**Source path:** `src/contracts/Contracts/`
**Source csproj:** `Contracts.csproj`

## Original fork provenance (Phase 01 fork, 2026-04-22)

This project is the internal RabbitMQ DTO fork consumed by the exchange and recorder
services. It was forked verbatim from Arena's `Contracts/` project in commit `b296bfa`
("feat(01-02): fork Arena Contracts/ into Bifrost.Contracts.Internal") as a bundled
prerequisite for the exchange donation that lands later in the same repo.

Copy rules applied at fork time:

- Entire folder copied verbatim from `Arena/src/contracts/Contracts/` at SHA
  `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`.
- Namespace rewrite: `Contracts.*` → `Bifrost.Contracts.Internal.*` (and the three
  sub-namespaces `Contracts.Commands` / `Contracts.Events` / `Contracts.Journal` rewrote
  to the matching `Bifrost.Contracts.Internal.Commands` / `.Events` / `.Journal`).
- csproj renamed: `Contracts.csproj` → `Bifrost.Contracts.Internal.csproj`.
- No GSD planning markers in source (pre-lint-scrubbed; `ci-scrub-gsd` + the earlier
  `ci-lint-fence-negative` both validate this).
- Dead-code DTOs retained per YAGNI-delete-later posture (see Divergences below).
- BIFROST-specific additions: `Shared/QuantityScale.cs` (decimal ↔ int64 ticks helper
  used by the gateway translator in Phase 07).

The repo-root provenance anchor `src/contracts-internal/UPSTREAM.md` carries the same
SHA + source-date; per-sub-folder `Commands/UPSTREAM.md`, `Events/UPSTREAM.md`, and
`Journal/UPSTREAM.md` carry per-folder notes. This project-level file consolidates and
extends those with the Phase 02 audit below.

## File inventory (31 .cs files total)

Arena's source tree contains **30 .cs files** at the upstream path; the fork adds
`Shared/QuantityScale.cs` for a total of **31** files on the BIFROST side.

Top-level (5):

- `Envelope.cs`
- `MessageTypes.cs`
- `InstrumentIdDto.cs`
- `RabbitMqResilience.cs`
- _(Shared/ subfolder below adds the BIFROST-only 5th file at project level)_

`Commands/` (5):

- `SubmitOrderCommand.cs`, `CancelOrderCommand.cs`, `ReplaceOrderCommand.cs`,
  `GetBookSnapshotRequest.cs`, `SubscribeCommand.cs`

`Events/` (19):

- Private-execution: `OrderAcceptedEvent.cs`, `OrderRejectedEvent.cs`,
  `OrderCancelledEvent.cs`, `OrderExecutedEvent.cs`,
  `MarketOrderRemainderCancelledEvent.cs`
- Public-market: `BookDeltaEvent.cs`, `BookLevelDto.cs`, `BookSnapshotResponse.cs`,
  `InstrumentAvailableEvent.cs`, `InstrumentListEvent.cs`, `ExchangeMetadataEvent.cs`,
  `PublicTradeEvent.cs`
- JSON infrastructure: `NanosecondStringConverter.cs`
- Arena dead-code (retained per YAGNI): `PublicOrderStatsEvent.cs`,
  `TraderMetricsSnapshot.cs`, `OrderEventDto.cs`, `LifecycleEventDto.cs`,
  `LifecycleHop.cs`, `HopType.cs`

`Journal/` (2, Arena dead-code retained per YAGNI):

- `IEventJournal.cs`, `JournalEntry.cs`

`Shared/` (1, BIFROST-only):

- `QuantityScale.cs`

## Divergences from Arena

- Namespace rewrite (described above).
- Dead-code DTOs retained: `PublicOrderStatsEvent`, `TraderMetricsSnapshot`,
  `OrderEventDto`, `LifecycleEventDto`, `LifecycleHop`, `HopType`,
  `Journal/IEventJournal`, `Journal/JournalEntry`. No downstream BIFROST consumer
  today — kept because inert strings and record definitions have zero runtime cost,
  removing them risks file-churn collisions with later phases that may consume them
  (phase-04 imbalance surface; phase-06 orchestrator lifecycle surface), and the
  existing translation-roundtrip tests in `tests/Bifrost.Contracts.Translation.Tests/`
  exercise the DTO surface via `Bifrost.Contracts.Internal` references that rely on
  the fork being at Arena parity.
- `Shared/QuantityScale.cs` added (BIFROST-only helper for the gRPC-↔-internal
  translator the Phase 07 gateway implements).

## Phase 02 audit note (2026-04-23)

The exchange-donation phase audited this fork against Arena commit
`5f8da6072978a4693de7c7ec7f5ff9ea22181a0b` and confirmed the file set is intact (31 `.cs`
files across `Commands/`, `Events/`, `Journal/`, `Shared/`, plus top-level `Envelope.cs`,
`MessageTypes.cs`, `InstrumentIdDto.cs`, `RabbitMqResilience.cs`).

No DTOs were added, removed, or modified in this audit. The dead-code DTOs retained by
the original fork (`Events/PublicOrderStatsEvent.cs`, `Events/TraderMetricsSnapshot.cs`,
`Events/OrderEventDto.cs`, `Events/LifecycleEventDto.cs`, `Events/LifecycleHop.cs`,
`Events/HopType.cs`, `Journal/IEventJournal.cs`, `Journal/JournalEntry.cs`) stay — inert
strings and record types have zero runtime cost, and later phases (04 imbalance, 06
orchestrator) may reference some of them.

### Consumers landing in this phase

The exchange Infrastructure.RabbitMq donation plan consumes: `SubmitOrderCommand`,
`CancelOrderCommand`, `ReplaceOrderCommand`, `GetBookSnapshotRequest`, `SubscribeCommand`,
`OrderAcceptedEvent`, `OrderRejectedEvent`, `OrderCancelledEvent`, `OrderExecutedEvent`,
`MarketOrderRemainderCancelledEvent`, `BookDeltaEvent`, `BookLevelDto`,
`BookSnapshotResponse`, `InstrumentAvailableEvent`, `InstrumentListEvent`,
`ExchangeMetadataEvent`, `PublicTradeEvent`, `Envelope`, `MessageTypes`,
`InstrumentIdDto`, `RabbitMqResilience`.

The recorder Infrastructure donation plan consumes the same event surface plus
`NanosecondStringConverter` (via `RecorderJsonContext`).

### Scope split documentation

The Phase 01 / Phase 02 contracts scope split is documented in
`bifrost-exchange/docs/phase-sequencing-note.md`.
