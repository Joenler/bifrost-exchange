namespace Bifrost.Recorder.Infrastructure;

/// <summary>
/// RabbitMQ topology constants the recorder binds to. Topic exchange name is
/// renamed from Arena's <c>trader.events.v1</c> to the BIFROST
/// <c>bifrost.events.v1</c> convention (see Plan 02-05 command-side rename).
/// Arena's <c>trader.metrics</c> exchange + recorder-metrics queue are DROPPED
/// — Phase 02 has no trader-metrics stream, so there is nothing to bind to.
/// Routing keys (<c>order.#</c>, <c>lifecycle.#</c>) stay identical.
/// </summary>
public static class RecorderTopology
{
    public const string TraderEventsExchange = "bifrost.events.v1";
    public const string RecorderEventsQueue = "bifrost.recorder.events.v1";
    public const string OrderRoutingKey = "order.#";
    public const string LifecycleRoutingKey = "lifecycle.#";

    /// <summary>
    /// The private topic exchange owned by the exchange service (see
    /// <c>Bifrost.Exchange.Infrastructure.RabbitMq.RabbitMqTopology.PrivateExchange</c>).
    /// Duplicated here as a string constant to avoid a cross-project
    /// reference from the recorder into the exchange infra assembly —
    /// the recorder binds against this exchange by name only.
    /// </summary>
    public const string PrivateExchange = "bifrost.private";

    /// <summary>
    /// Routing pattern the recorder binds its events queue to on the
    /// <c>bifrost.private</c> topic exchange so per-team ImbalanceSettlement
    /// rows fan into the recorder alongside the Arena-style order/lifecycle
    /// traffic. Wildcard suffix matches every <c>clientId</c>.
    /// </summary>
    public const string ImbalanceSettlementRoutingKey = "private.imbalance.settlement.#";
}
