using RabbitMQ.Client;

namespace Bifrost.DahAuction.Rabbit;

/// <summary>
/// Auction-owned RabbitMQ topology: one NEW direct exchange (<c>bifrost.auction</c>)
/// for clearing-payload fan-out to the Gateway service, plus routing-key helpers
/// for audit events emitted on the EXISTING <c>bifrost.public</c> exchange
/// (where the quoter already emits <c>events.regime.change</c>; recorder
/// binding picks up <c>events.auction.*</c> via the wildcard <c>events.#</c>).
/// </summary>
/// <remarks>
/// Split-exchange rationale: publish per-team clearing fan-out on
/// <see cref="AuctionExchange"/> (Gateway consumer) and public audit events
/// on <c>bifrost.public</c> (recorder consumer binds on <c>events.#</c>) so
/// consumers are uncoupled — the Gateway never sees audit traffic, and the
/// recorder never sees per-team clearing payloads. The public summary row
/// emitted on the audit bus carries no per-team identity (TeamName = null
/// on the DTO); per-team awards flow only on the direct <c>bifrost.auction</c>
/// exchange.
/// </remarks>
public static class AuctionRabbitTopology
{
    /// <summary>
    /// Direct exchange carrying <c>ClearingResultDto</c> payloads (public summary
    /// row + per-team rows). Routing-key format is supplied by
    /// <see cref="AuctionClearedRoutingKey"/>.
    /// </summary>
    public const string AuctionExchange = "bifrost.auction";

    /// <summary>
    /// Routing key on <c>bifrost.public</c> for auction-bid audit events.
    /// Mirror pattern of <c>Bifrost.Quoter.Rabbit.QuoterRabbitTopology.EventsRoutingKeyRegimeChange</c>.
    /// </summary>
    public const string EventsRoutingKeyAuctionBid = "events.auction.bid";

    /// <summary>
    /// Routing key on <c>bifrost.public</c> for auction-clearing audit events
    /// (one per summary + per-team row). The recorder's <c>events</c> table
    /// stores the envelope payload as <c>payload_json</c>.
    /// </summary>
    public const string EventsRoutingKeyAuctionCleared = "events.auction.cleared";

    /// <summary>
    /// Routing key on <c>bifrost.public</c> for no-cross audit events — emitted
    /// once per QH that fails to cross (demand curve does not meet supply curve
    /// with positive volume).
    /// </summary>
    public const string EventsRoutingKeyAuctionNoCross = "events.auction.no_cross";

    /// <summary>
    /// Routing key for <c>ClearingResult</c> messages on
    /// <see cref="AuctionExchange"/>. Parameterised by quarter instrument id so
    /// the Gateway can bind per-quarter if it so chooses; this service publishes
    /// against the fully qualified id string.
    /// </summary>
    public static string AuctionClearedRoutingKey(string quarterId) =>
        $"bifrost.auction.cleared.{quarterId}";

    /// <summary>
    /// ClientId carried on the Envelope for every outbound auction message
    /// (matches the service-level ClientId convention used across the exchange
    /// bus; see also Quoter's <c>bifrost-quoter</c> / <c>quoter</c> usage).
    /// </summary>
    public const string ClientId = "dah-auction";

    /// <summary>
    /// Declares the direct <see cref="AuctionExchange"/>. Idempotent; safe to
    /// call on every boot. The existing <c>bifrost.public</c> exchange is
    /// already declared by the central exchange service on its own boot; this
    /// service does NOT re-declare it here.
    /// </summary>
    public static Task DeclareAuctionExchangeAsync(IChannel channel, CancellationToken ct = default) =>
        channel.ExchangeDeclareAsync(AuctionExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
}
