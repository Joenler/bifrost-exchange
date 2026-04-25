namespace Bifrost.Recorder.Infrastructure;

/// <summary>
/// RabbitMQ topology constants the recorder binds to. Topic exchange name is
/// renamed from Arena's <c>trader.events.v1</c> to the BIFROST
/// <c>bifrost.events.v1</c> convention. Arena's <c>trader.metrics</c> exchange
/// + recorder-metrics queue are DROPPED — there is no trader-metrics stream
/// in BIFROST so there is nothing to bind to. Routing keys
/// (<c>order.#</c>, <c>lifecycle.#</c>) stay identical for compatibility with
/// any future direct order/lifecycle publisher.
/// </summary>
/// <remarks>
/// The recorder queue is bound to four sources, all multiplexed into the
/// single <see cref="RecorderEventsQueue"/>:
/// <list type="number">
/// <item>
/// Legacy trader events on <see cref="TraderEventsExchange"/> with routing
/// keys <see cref="OrderRoutingKey"/> and <see cref="LifecycleRoutingKey"/>.
/// Historical from the Arena donation; no current publisher targets this
/// exchange, but the bindings stay so a future direct order-lifecycle
/// publisher can reintroduce traffic without recorder changes.
/// </item>
/// <item>
/// Per-team imbalance settlement events on <see cref="PrivateExchange"/>
/// with routing pattern <see cref="ImbalanceSettlementRoutingKey"/> — the
/// imbalance simulator's gate-time settlement output.
/// </item>
/// <item>
/// Public audit events on <see cref="PublicEventsExchange"/> with routing
/// pattern <see cref="PublicEventsRoutingKey"/>. Catches every
/// <c>events.*</c> emission on the public bus: the auction service's
/// <c>events.auction.bid</c> / <c>events.auction.cleared</c> /
/// <c>events.auction.no_cross</c> rows, the quoter's
/// <c>events.regime.change</c> rows, and any future public audit publishers.
/// All land in the existing <c>events</c> table without schema change.
/// </item>
/// <item>
/// MC command audit envelopes on <see cref="McAuditExchange"/> with routing
/// pattern <see cref="McCommandRoutingPattern"/> (Phase 06 D-23). Every
/// orchestrator-published <c>McCommandLog</c> — accepted or rejected — fans
/// into the recorder queue and lands in the Phase 02-shipped (empty)
/// <c>mc_commands</c> table.
/// </item>
/// </list>
/// </remarks>
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

    /// <summary>
    /// The public audit-events topic exchange. Declared by the central
    /// exchange service on its boot
    /// (<c>Bifrost.Exchange.Infrastructure.RabbitMq.RabbitMqTopology.PublicExchange</c>);
    /// duplicated here as a string constant to keep the recorder free of a
    /// cross-project reference into the exchange infra assembly.
    /// </summary>
    public const string PublicEventsExchange = "bifrost.public";

    /// <summary>
    /// Routing-key pattern covering every <c>events.*</c> audit-event key
    /// emitted on <see cref="PublicEventsExchange"/>. Catches the auction
    /// service's <c>events.auction.bid</c> / <c>events.auction.cleared</c> /
    /// <c>events.auction.no_cross</c> rows, the quoter's
    /// <c>events.regime.change</c> rows, and any future public audit
    /// publishers — all multiplexed into the existing <c>events</c> table
    /// via <see cref="RecorderEventsQueue"/>.
    /// </summary>
    public const string PublicEventsRoutingKey = "events.#";

    /// <summary>
    /// MC command audit-stream topic exchange (Phase 06 D-23). Owned by the
    /// orchestrator (see
    /// <c>Bifrost.Orchestrator.Rabbit.OrchestratorRabbitMqTopology.McAuditExchange</c>);
    /// duplicated here as a string constant to keep the recorder free of a
    /// cross-project reference into the orchestrator assembly. The
    /// orchestrator publishes one <c>McCommandLog</c> envelope per processed
    /// <c>McCommand</c> — accepted or rejected — on this exchange.
    /// </summary>
    public const string McAuditExchange = "bifrost.mc.v1";

    /// <summary>
    /// Routing-key pattern bound to <see cref="RecorderEventsQueue"/> against
    /// <see cref="McAuditExchange"/>. Wildcard subset of the orchestrator's
    /// per-command keys (<c>mc.command.{cmd_snake}</c>) — every command
    /// audit envelope multiplexes into the existing recorder queue and
    /// lands in the Phase 02-shipped (empty) <c>mc_commands</c> table
    /// without a schema migration (D-12 zero-migrations posture).
    /// </summary>
    public const string McCommandRoutingPattern = "mc.command.#";
}
