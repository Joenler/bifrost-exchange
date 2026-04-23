# src/contracts-internal/

Internal RabbitMQ DTO records forked verbatim from Arena's `Contracts/` project (see
`UPSTREAM.md` for source repo, commit SHA, and provenance notes). These records are
the wire types that circulate between central-machine services over RabbitMQ; they are
**never** sent across the team-facing gRPC boundary.

The gateway translates between the gRPC surface under `../contracts/` and these internal
DTOs. Translation rules live in `docs/gateway-mapping.md`.
