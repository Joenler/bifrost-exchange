# UPSTREAM — Bifrost.Exchange.Infrastructure.RabbitMq

**Upstream repo:** Arena
**Source path:** `src/exchange/Exchange.Infrastructure.RabbitMq/`
**Upstream SHA (HEAD at port):** `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`
**Ported at:** Phase 02 Plan 05
**Target namespace:** `Bifrost.Exchange.Infrastructure.RabbitMq`

## Per-file inventory

| File | Mutations applied |
|------|-------------------|
| `RabbitMqTopology.cs` | Namespace rewrite; 4 exchange/queue-name constants renamed to `bifrost.*`; `PrivateQueueName` template rewired to `bifrost.private.v1.{clientId}`. Routing-key constants + helpers UNCHANGED. |
| `RabbitMqEventPublisher.cs` | Namespace rewrite; `using Contracts{,.Events}` → `using Bifrost.Contracts.Internal{,.Events}`; `using Exchange.Application` → `using Bifrost.Exchange.Application`; added `using Bifrost.Time`; primary-ctor extended `(IChannel channel, IClock clock)`; 7 `DateTimeOffset.UtcNow` sites rewired to `clock.GetUtcNow()`. |
| `CommandConsumerService.cs` | Namespace rewrite; `using Contracts.Commands` → `using Bifrost.Contracts.Internal.Commands`; `using Exchange.Application` → `using Bifrost.Exchange.Application`; added `using Bifrost.Time`; primary-ctor extended with `IClock clock` as 3rd parameter; 2 `DateTimeOffset.UtcNow` sites rewired; Activity tag keys `arena.routing_key` / `arena.correlation_id` renamed to `bifrost.*` (naming hygiene — OTel tag keys only; does NOT affect AMQP routing). |
| `BufferedEventPublisher.cs` | Namespace rewrite; `using Exchange.Application` → `using Bifrost.Exchange.Application`. No clock references, no string literals changed. Bounded(8192) + DropOldest preserved verbatim. |
| `ExchangeActivitySource.cs` | Namespace rewrite; `ActivitySource("Arena.Exchange", ...)` → `ActivitySource("Bifrost.Exchange", ...)`; `[assembly: InternalsVisibleTo("Exchange.Application.Tests")]` → `[assembly: InternalsVisibleTo("Bifrost.Exchange.Tests")]` (REAL BIFROST test assembly name). |

## Topic-rename table (D-03; exchange names + templates only — routing keys UNCHANGED)

| Arena | BIFROST |
|-------|---------|
| `"exchange.cmd"` | `"bifrost.cmd"` |
| `"exchange.public"` | `"bifrost.public"` |
| `"exchange.private"` | `"bifrost.private"` |
| `"exchange.cmd.v1"` | `"bifrost.cmd.v1"` |
| `$"exchange.private.v1.{clientId}"` | `$"bifrost.private.v1.{clientId}"` |
| `"Arena.Exchange"` (ActivitySource) | `"Bifrost.Exchange"` |
| `InternalsVisibleTo("Exchange.Application.Tests")` | `InternalsVisibleTo("Bifrost.Exchange.Tests")` |

**Routing keys preserved byte-identical:** `cmd.order.submit`, `cmd.order.cancel`, `cmd.order.replace`, `cmd.inquiry.book`, `cmd.client.subscribe`, `cmd.#` (queue bind), `public.instruments.available`, `public.book.delta.{instrument}`, `public.trade.{instrument}`, `public.book.snapshot.{instrument}`, `public.orderstats.{instrument}`, `private.order.{clientId}.{eventType}`, `private.exec.{clientId}.{eventType}`, `private.order.{clientId}.metadata`, `private.order.{clientId}.instruments`, `private.inquiry.{clientId}.book`.

## Clock-rewire site inventory (measured pre-port)

| File | Arena `DateTimeOffset.UtcNow` sites | Post-port | Ctor addition |
|------|-------------------------------------|-----------|----------------|
| `RabbitMqEventPublisher.cs` | 7 | 7× `clock.GetUtcNow()` | `+IClock clock` (2nd primary-ctor param) |
| `CommandConsumerService.cs` | 2 | 2× `clock.GetUtcNow()` | `+IClock clock` (3rd primary-ctor param) |
| `BufferedEventPublisher.cs` | 0 | 0 | — |
| `RabbitMqTopology.cs` | 0 | 0 | — |
| `ExchangeActivitySource.cs` | 0 | 0 | — |
| **Total** | **9** | **9** | — |

Post-rewire invariant: `rg -n 'DateTime(Offset)?\.UtcNow' src/exchange/Exchange.Infrastructure.RabbitMq/` returns 0 (W5 zero-check).

## Notes

- `ExchangeType.Topic`, `durable: true`, `autoDelete: false`, and `cmd.#` bind pattern are preserved verbatim.
- `BufferedEventPublisher` bounded channel capacity stays at 8192 with `BoundedChannelFullMode.DropOldest` (RESEARCH Pitfall 3).
- `RabbitMQ.Client.OpenTelemetry` Arena PackageReference dropped — not wired through BIFROST's observability surface yet (Phase 11 concern).
- `InternalsVisibleTo` now targets `Bifrost.Exchange.Tests`, the REAL BIFROST test project (verified at `tests/Bifrost.Exchange.Tests/`). Not `Bifrost.Exchange.Application.Tests` (which does not exist) and not Arena's `Exchange.Application.Tests`.
- Activity tag keys `arena.routing_key` / `arena.correlation_id` were renamed to `bifrost.*` as a naming-hygiene deviation (Rule 2). OTel tag keys are observability metadata; they do not affect AMQP routing, so this is additive and not on the `exchange.cmd`/`exchange.public`/`exchange.private` negative-gate list.
