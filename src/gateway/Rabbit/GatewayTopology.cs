using Bifrost.Exchange.Infrastructure.RabbitMq;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Gateway-owned topology constants. Reuses
/// <see cref="RabbitMqTopology"/> for the shared exchange/queue
/// definitions established in Phase 02; this class declares only the
/// additional surface the gateway introduces:
///   - gateway heartbeat routing key (on the existing bifrost.public exchange),
///   - per-team private queue name template (gateway-local consumer queues),
///   - gateway-local public-events / auction-results / round-state queues.
///
/// Plan 06 binds the per-team private queue to <see cref="RabbitMqTopology.PrivateExchange"/>
/// using the team's clientId as the routing-key prefix; the heartbeat routing
/// key publishes onto <see cref="RabbitMqTopology.PublicExchange"/> per Phase 06 D-19.
/// </summary>
public static class GatewayTopology
{
    /// <summary>Heartbeat routing key — gateway publishes <see cref="MessageType.GatewayHeartbeat"/> at the configured cadence on this key.</summary>
    public const string HeartbeatRoutingKey = "gateway.heartbeat";

    /// <summary>Heartbeat goes onto the existing public exchange — no new exchange to declare.</summary>
    public const string HeartbeatExchange = RabbitMqTopology.PublicExchange;

    /// <summary>Gateway-local consumer queue for the public bus (book.delta.#, trade.#, public.forecast, etc.). Plan 06 binds.</summary>
    public const string PublicEventsQueue = "bifrost.gateway.public";

    /// <summary>Gateway-local consumer queue for round-state transitions. Plan 06 binds.</summary>
    public const string RoundStateQueue = "bifrost.gateway.roundstate";

    /// <summary>Gateway-local consumer queue for auction clearing-result fan-out. Plan 06 binds.</summary>
    public const string AuctionResultQueue = "bifrost.gateway.auction";

    /// <summary>Per-team private queue name. Plan 06 binds this to bifrost.private.# for the team's clientId.</summary>
    public static string PerTeamPrivateQueue(string clientId) => $"bifrost.gateway.private.{clientId}";

    /// <summary>
    /// Local mirror of <c>Bifrost.DahAuction.Rabbit.AuctionRabbitTopology.AuctionExchange</c>
    /// — the gateway must not depend on the dah-auction Web SDK host project just to
    /// read a constant. Same value (<c>"bifrost.auction"</c>) is the contract.
    /// </summary>
    public const string AuctionExchange = "bifrost.auction";

    /// <summary>
    /// Mirrors <c>Bifrost.DahAuction.Rabbit.AuctionRabbitTopology.AuctionClearedRoutingKey</c>.
    /// Direct exchange + per-quarter routing key.
    /// </summary>
    public static string AuctionClearedRoutingKey(string quarterId) => $"bifrost.auction.cleared.{quarterId}";

    /// <summary>
    /// Local mirror of <c>Bifrost.Orchestrator.Rabbit.OrchestratorRabbitMqTopology.RoundExchange</c>
    /// — the gateway must not depend on the orchestrator host project just to read a constant.
    /// Same value (<c>"bifrost.round.v1"</c>) is the contract.
    /// </summary>
    public const string RoundExchange = "bifrost.round.v1";
}
