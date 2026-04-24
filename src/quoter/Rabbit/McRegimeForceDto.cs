using Bifrost.Quoter.Schedule;

namespace Bifrost.Quoter.Rabbit;

/// <summary>
/// JSON DTO carried on <c>bifrost.mc.regime</c>. The orchestrator (Phase 06)
/// serializes this from the gRPC <c>mc.proto::McCommand.RegimeForce</c>
/// variant; the quoter's <see cref="McRegimeForceConsumer"/> deserializes it
/// and forwards a <see cref="RegimeForceMessage"/> into the schedule's inbox.
/// <see cref="Nonce"/> is the orchestrator-issued idempotency discriminator
/// the schedule's <c>LruSet</c> uses to drop duplicate redeliveries
/// (RabbitMQ at-least-once semantics).
/// </summary>
public sealed record McRegimeForceDto(Regime Regime, Guid Nonce);
