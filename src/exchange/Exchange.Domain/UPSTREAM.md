# Upstream — src/exchange/Exchange.Domain/

Arena commit SHA: `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`
Arena source root: `src/exchange/Exchange.Domain/`
Copied: 2026-04-23
Port action: COPY-VERBATIM x 22 files + 3-value enum extension (see Divergences).

Note on SHA: the original planning docs referenced `5f8da6072978a4693de7c7ec7f5ff9ea22181a0b`
as the Arena HEAD at initial donation (see root `UPSTREAM.md`). Arena has advanced since; this
folder records the CURRENT Arena SHA at port time per the fork-as-of-port semantics called out
in the plan body ("If different, record the CURRENT SHA"). The root `UPSTREAM.md` anchor stays
historically accurate for Plan 01 (Bifrost.Time) which was ported against the older SHA.

## Files

| Donated path (this folder)      | Original path (Arena)                                          | Arena SHA                                    | Mutations |
|---------------------------------|----------------------------------------------------------------|----------------------------------------------|-----------|
| `MatchingEngine.cs`             | `src/exchange/Exchange.Domain/MatchingEngine.cs`               | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace `Exchange.Domain` -> `Bifrost.Exchange.Domain`. |
| `OrderBook.cs`                  | `src/exchange/Exchange.Domain/OrderBook.cs`                    | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `Order.cs`                      | `src/exchange/Exchange.Domain/Order.cs`                        | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `PriceLevel.cs`                 | `src/exchange/Exchange.Domain/PriceLevel.cs`                   | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `RejectionCode.cs`              | `src/exchange/Exchange.Domain/RejectionCode.cs`                | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite + append 19th enum value `ExchangeClosed` (D-11). |
| `RejectionCodeNames.cs`         | `src/exchange/Exchange.Domain/RejectionCodeNames.cs`           | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite + append `ExchangeClosed = "ExchangeClosed"` static + `RejectionCode.ExchangeClosed => ExchangeClosed` switch arm. |
| `RejectionCodeExtensions.cs`    | `src/exchange/Exchange.Domain/RejectionCodeExtensions.cs`      | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite + switch arm `ExchangeClosed => "Exchange closed at gate"`. |
| `MatchingEvents.cs`             | `src/exchange/Exchange.Domain/MatchingEvents.cs`               | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `MatchingResult.cs`             | `src/exchange/Exchange.Domain/MatchingResult.cs`               | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `MonotonicSequenceGenerator.cs` | `src/exchange/Exchange.Domain/MonotonicSequenceGenerator.cs`   | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `ISequenceGenerator.cs`         | `src/exchange/Exchange.Domain/ISequenceGenerator.cs`           | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `ClientId.cs`                   | `src/exchange/Exchange.Domain/ClientId.cs`                     | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `OrderId.cs`                    | `src/exchange/Exchange.Domain/OrderId.cs`                      | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `TradeId.cs`                    | `src/exchange/Exchange.Domain/TradeId.cs`                      | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `SequenceNumber.cs`             | `src/exchange/Exchange.Domain/SequenceNumber.cs`               | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `InstrumentId.cs`               | `src/exchange/Exchange.Domain/InstrumentId.cs`                 | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `DeliveryArea.cs`               | `src/exchange/Exchange.Domain/DeliveryArea.cs`                 | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `DeliveryPeriod.cs`             | `src/exchange/Exchange.Domain/DeliveryPeriod.cs`               | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `Price.cs`                      | `src/exchange/Exchange.Domain/Price.cs`                        | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `Quantity.cs`                   | `src/exchange/Exchange.Domain/Quantity.cs`                     | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `Side.cs`                       | `src/exchange/Exchange.Domain/Side.cs`                         | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `OrderStatus.cs`                | `src/exchange/Exchange.Domain/OrderStatus.cs`                  | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `OrderType.cs`                  | `src/exchange/Exchange.Domain/OrderType.cs`                    | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `IcebergRefresh.cs`             | `src/exchange/Exchange.Domain/IcebergRefresh.cs`               | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |
| `Trade.cs`                      | `src/exchange/Exchange.Domain/Trade.cs`                        | `8419a0c312f1dc0314a45e2cd765f0fd6f18dbc0`   | Namespace rewrite only. |

## Divergences

1. **`RejectionCode` enum extended by one value (`ExchangeClosed`).** This is the 19th value and
   lands here (not a new file) so every consumer of the existing 18 values compiles without
   change. The new value is the landing point for the RoundState-gate reject authored in the
   Exchange.Application donation plan. Sidecar string vocabulary (`"gate_reached"`,
   `"round_not_started"`, ...) lives on `OrderValidationResult.RejectionReason`, NOT as enum
   values - the enum stays coarse, the detail stays string-typed.
2. **Iceberg/FOK code paths preserved in `MatchingEngine.cs` verbatim but not wire-exposed.**
   The exchange RabbitMQ command surface in the Infrastructure donation plan does not route
   `SubmitIceberg` / `SubmitFOK` commands - the gateway SDK does in a later phase.
3. **Namespace rewrite** on every file: `Exchange.Domain` -> `Bifrost.Exchange.Domain`.

## Commit SHA lookup

```bash
git -C /Users/jonathanjonler/RiderProjects/Arena log -1 --format=%H -- src/exchange/Exchange.Domain/
```

Re-run at port time; overwrite the SHA above if Arena has advanced since the donation date.
