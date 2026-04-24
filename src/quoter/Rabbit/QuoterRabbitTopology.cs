namespace Bifrost.Quoter.Rabbit;

/// <summary>
/// Quoter-owned RabbitMQ exchange / queue / routing-key constants. Reuses the
/// shared <c>Bifrost.Exchange.Infrastructure.RabbitMq.RabbitMqTopology</c> for
/// the outbound command exchange and routing keys -- this class only declares
/// the additional surface the quoter introduces:
///   - the inbound MC regime-force exchange + queue,
///   - the routing key under which the quoter publishes regime-change events
///     onto the public events bus.
/// </summary>
public static class QuoterRabbitTopology
{
    /// <summary>
    /// MC command fanout exchange. The orchestrator (Phase 06) publishes
    /// <c>McRegimeForceDto</c> messages here on routing key
    /// <see cref="McRegimeRoutingKey"/>; the quoter's <c>McRegimeForceConsumer</c>
    /// is the only subscriber.
    /// </summary>
    public const string McRegimeExchange = "bifrost.mc";

    /// <summary>
    /// Quoter-owned queue that holds inbound MC regime-force commands. Declared
    /// as a non-durable, non-exclusive, auto-delete queue because MC commands
    /// must not survive a quoter restart -- a fresh round always starts at the
    /// scenario beat schedule, never at a stale forced regime.
    /// </summary>
    public const string McRegimeQueue = "bifrost.mc.regime";

    /// <summary>
    /// Routing key used by the orchestrator when publishing MC regime-force
    /// commands. The quoter binds <see cref="McRegimeQueue"/> to
    /// <see cref="McRegimeExchange"/> on this key.
    /// </summary>
    public const string McRegimeRoutingKey = "mc.regime.force";

    /// <summary>
    /// Routing key used when the quoter publishes
    /// <c>Event.RegimeChange</c> onto the shared public events exchange
    /// (<c>Bifrost.Exchange.Infrastructure.RabbitMq.RabbitMqTopology.PublicExchange</c>).
    /// </summary>
    public const string EventsRoutingKeyRegimeChange = "events.regime.change";
}
