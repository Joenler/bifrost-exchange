using System.Text;
using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Internal.McLog;
using Bifrost.Time;
using RabbitMQ.Client;

namespace Bifrost.Orchestrator.Rabbit;

/// <summary>
/// Envelope-wrapping publisher for the orchestrator's six publication
/// surfaces:
///
///   - bifrost.round.v1 / round.state.{snake}   — RoundStateChanged
///   - bifrost.mc.v1    / mc.command.{snake}     — McCommandLog audit
///   - bifrost.events.v1 / events.news           — NewsPayload
///   - bifrost.events.v1 / events.market_alert   — MarketAlertPayload
///   - bifrost.events.v1 / events.config_change  — ConfigChangePayload
///   - bifrost.mc       / mc.regime.force        — McRegimeForceDto (D-14
///     amendment: orchestrator ROUTES, does not emit events.regime_change;
///     quoter consumes, installs the regime, cancels-all + re-quotes, then
///     publishes Event.RegimeChange per Phase 03 D-17)
///
/// Every payload is wrapped in <see cref="Envelope{T}"/> with
/// <c>TimestampUtc = IClock.GetUtcNow()</c> and a typed
/// <see cref="MessageTypes"/> discriminator string. JSON serialisation uses
/// camelCase property naming — wire-identical to the Phase 02
/// <c>RabbitMqEventPublisher</c> so every existing envelope consumer
/// deserialises the orchestrator's publishes without change.
///
/// Thread-safety: driven from the orchestrator actor's single-reader drain
/// loop; RabbitMQ.Client 7.x IChannel is NOT thread-safe, so callers must
/// serialise access.
/// </summary>
public sealed class OrchestratorPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IChannel _channel;
    private readonly IClock _clock;

    public OrchestratorPublisher(IChannel channel, IClock clock)
    {
        _channel = channel;
        _clock = clock;
    }

    /// <summary>
    /// Publish a RoundStateChanged envelope on
    /// <see cref="OrchestratorRabbitMqTopology.RoundExchange"/> with routing
    /// key <c>round.state.{state_snake}</c>. The caller supplies the
    /// monotonic <paramref name="sequence"/>; the actor owns the per-exchange
    /// counter so reconciliation publishes on restart continue the sequence
    /// from the persisted snapshot.
    /// </summary>
    public async ValueTask PublishRoundStateChangedAsync(
        RoundStateChangedPayload payload,
        long sequence,
        CancellationToken ct = default)
    {
        string routingKey = OrchestratorRabbitMqTopology.RoundStateRoutingKey(
            OrchestratorRabbitMqTopology.ToSnakeCase(payload.State));

        Envelope<object> envelope = new(
            MessageType: MessageTypes.RoundStateChanged,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: null,
            ClientId: null,
            InstrumentId: null,
            Sequence: sequence,
            Payload: payload);

        await PublishAsync(OrchestratorRabbitMqTopology.RoundExchange, routingKey, envelope, ct);
    }

    /// <summary>
    /// Publish an McCommandLog envelope on
    /// <see cref="OrchestratorRabbitMqTopology.McAuditExchange"/> with routing
    /// key <c>mc.command.{cmd_snake}</c>. Accepted and rejected commands are
    /// both audit-logged — rejection payloads carry Success=false + rejection
    /// detail in Message + NewStateJson="".
    /// </summary>
    public async ValueTask PublishMcCommandLogAsync(
        McCommandLogPayload payload,
        string commandSnakeCase,
        CancellationToken ct = default)
    {
        string routingKey = OrchestratorRabbitMqTopology.McCommandRoutingKey(commandSnakeCase);

        Envelope<object> envelope = new(
            MessageType: MessageTypes.McCommandLog,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: null,
            ClientId: null,
            InstrumentId: null,
            Sequence: null,
            Payload: payload);

        await PublishAsync(OrchestratorRabbitMqTopology.McAuditExchange, routingKey, envelope, ct);
    }

    /// <summary>
    /// Publish a News envelope on
    /// <see cref="OrchestratorRabbitMqTopology.EventsExchange"/> with routing
    /// key <c>events.news</c>. Emitted on NewsFireCmd (canned library hit —
    /// <c>LibraryKey</c> non-empty) and NewsPublishCmd
    /// (<c>LibraryKey=""</c>).
    /// </summary>
    public ValueTask PublishNewsAsync(NewsPayload payload, CancellationToken ct = default) =>
        PublishEventAsync(
            MessageTypes.News,
            OrchestratorRabbitMqTopology.EventsNewsRoutingKey,
            payload,
            ct);

    /// <summary>
    /// Publish a MarketAlert envelope on
    /// <see cref="OrchestratorRabbitMqTopology.EventsExchange"/> with routing
    /// key <c>events.market_alert</c>. Emitted on AlertUrgentCmd and on the
    /// Abort auto-emit path (round-aborted alert per SPEC Req 12).
    /// </summary>
    public ValueTask PublishMarketAlertAsync(MarketAlertPayload payload, CancellationToken ct = default) =>
        PublishEventAsync(
            MessageTypes.MarketAlert,
            OrchestratorRabbitMqTopology.EventsMarketAlertRoutingKey,
            payload,
            ct);

    /// <summary>
    /// Publish a ConfigChange envelope on
    /// <see cref="OrchestratorRabbitMqTopology.EventsExchange"/> with routing
    /// key <c>events.config_change</c>. Emitted on ConfigSetCmd.
    /// </summary>
    public ValueTask PublishConfigChangeAsync(ConfigChangePayload payload, CancellationToken ct = default) =>
        PublishEventAsync(
            MessageTypes.ConfigChange,
            OrchestratorRabbitMqTopology.EventsConfigChangeRoutingKey,
            payload,
            ct);

    /// <summary>
    /// Publish a PhysicalShock envelope on
    /// <see cref="OrchestratorRabbitMqTopology.EventsExchange"/> with routing
    /// key <c>events.physical_shock</c>. Emitted from the orchestrator's
    /// NewsFireCmd path when the resolved canned-library entry carries an
    /// optional shock payload (per ADR-0005 canned-library format).
    /// </summary>
    /// <remarks>
    /// Distinct from the imbalance simulator's <c>PhysicalShockEvent</c> DTO
    /// (sibling on the same internal namespace) which covers the operator-
    /// injected <c>PhysicalShockCmd</c> path with a required QuarterIndex +
    /// TimestampNs. This method's payload — <see cref="PhysicalShockPayload"/>
    /// — leaves QuarterIndex nullable because news-library shocks carry no
    /// target-quarter hint. Both DTOs publish on the same exchange + routing
    /// key; downstream consumers select the deserializer that matches their
    /// subscription.
    /// </remarks>
    public ValueTask PublishPhysicalShockAsync(PhysicalShockPayload payload, CancellationToken ct = default) =>
        PublishEventAsync(
            MessageTypes.PhysicalShock,
            OrchestratorRabbitMqTopology.EventsPhysicalShockRoutingKey,
            payload,
            ct);

    /// <summary>
    /// Publish an McRegimeForceDto envelope on the Phase-03-owned
    /// <see cref="OrchestratorRabbitMqTopology.QuoterMcExchange"/>
    /// (<c>bifrost.mc</c>, NOT <c>bifrost.mc.v1</c>) with routing key
    /// <see cref="OrchestratorRabbitMqTopology.QuoterMcRegimeRoutingKey"/>
    /// (<c>mc.regime.force</c>). Per D-14 amendment: orchestrator ROUTES; the
    /// quoter consumes this message, installs the regime, cancels-all +
    /// re-quotes, and publishes <c>events.regime_change</c> (Phase 03 D-17
    /// locks the quoter as the sole <c>Event.RegimeChange</c> emitter).
    /// </summary>
    /// <param name="mcRegimeForcePayload">
    /// An <c>McRegimeForceDto</c>-shaped object — declared as <see cref="object"/>
    /// to avoid a <c>Bifrost.Orchestrator → Bifrost.Quoter</c>
    /// ProjectReference (the quoter already consumes orchestrator-originated
    /// messages on the wire, so a strict csproj coupling would invert the
    /// dependency direction). The caller (OrchestratorActor) constructs the
    /// shape via anonymous record or a local DTO copy with matching
    /// camelCase JSON fields (<c>regime</c>, <c>nonce</c>).
    /// </param>
    public async ValueTask PublishRegimeForceAsync(
        object mcRegimeForcePayload,
        CancellationToken ct = default)
    {
        // Dedicated MessageType discriminator — not one of the five
        // MessageTypes constants Plan 02 landed because the quoter's
        // McRegimeForceConsumer deserialises via direct JSON into
        // McRegimeForceDto rather than through the envelope-MessageType
        // dispatcher the other routes use. A future Phase 03 alignment could
        // move this to a shared constant if the envelope dispatch path wins.
        Envelope<object> envelope = new(
            MessageType: "McRegimeForce",
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: null,
            ClientId: null,
            InstrumentId: null,
            Sequence: null,
            Payload: mcRegimeForcePayload);

        await PublishAsync(
            OrchestratorRabbitMqTopology.QuoterMcExchange,
            OrchestratorRabbitMqTopology.QuoterMcRegimeRoutingKey,
            envelope,
            ct);
    }

    private ValueTask PublishEventAsync<T>(
        string messageType,
        string routingKey,
        T payload,
        CancellationToken ct)
        where T : notnull
    {
        Envelope<object> envelope = new(
            MessageType: messageType,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: null,
            ClientId: null,
            InstrumentId: null,
            Sequence: null,
            Payload: payload);

        return PublishAsync(OrchestratorRabbitMqTopology.EventsExchange, routingKey, envelope, ct);
    }

    private async ValueTask PublishAsync(
        string exchange,
        string routingKey,
        Envelope<object> envelope,
        CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
        BasicProperties props = new() { ContentType = "application/json" };

        await _channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);
    }
}
