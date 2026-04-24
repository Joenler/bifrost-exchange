using System.Text;
using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Time;
using RabbitMQ.Client;

namespace Bifrost.DahAuction.Rabbit;

/// <summary>
/// Thin wrapper over <see cref="BufferedEventPublisher"/> plus a direct
/// <see cref="IChannel"/> for <c>bifrost.auction</c> clearing-payload fan-out.
/// All four emission methods are fire-and-forget: the audit-event path hands
/// off to <c>BufferedEventPublisher</c> (bounded channel + drain task); the
/// direct-to-auction path uses an async <c>BasicPublishAsync</c> off the
/// actor loop's drain thread (see Pitfall P-03: RabbitMQ.Client 7.x channels
/// are NOT thread-safe, so this publisher owns a dedicated IChannel).
/// </summary>
/// <remarks>
/// Wire-envelope convention: every outbound message is wrapped in
/// <see cref="Envelope{T}"/>:
///   - <c>MessageType</c> = one of <see cref="MessageTypes.AuctionBidSubmitted"/>,
///     <see cref="MessageTypes.AuctionClearingResult"/>, or
///     <see cref="MessageTypes.AuctionNoCross"/>
///   - <c>ClientId</c> = <see cref="AuctionRabbitTopology.ClientId"/> (<c>"dah-auction"</c>)
///   - <c>TimestampUtc</c> = <see cref="IClock.GetUtcNow"/>
///   - <c>InstrumentId</c> = the quarter id on clearing payloads; null on audit rows
///   - <c>CorrelationId</c>, <c>Sequence</c> = null (unused in this surface)
///
/// Serialisation uses the same camelCase <see cref="JsonSerializerOptions"/> as
/// <c>RabbitMqEventPublisher</c> so both publishers produce wire-identical
/// envelope shapes.
/// </remarks>
public sealed class AuctionPublisher : IAuctionPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IChannel _directChannel;
    private readonly BufferedEventPublisher _events;
    private readonly IClock _clock;

    public AuctionPublisher(IChannel directChannel, BufferedEventPublisher events, IClock clock)
    {
        _directChannel = directChannel;
        _events = events;
        _clock = clock;
    }

    /// <summary>
    /// Publishes a <see cref="ClearingResultDto"/> envelope on
    /// <see cref="AuctionRabbitTopology.AuctionExchange"/> with routing key
    /// <see cref="AuctionRabbitTopology.AuctionClearedRoutingKey"/>. Called
    /// once per summary row and once per per-team row; the caller emits the
    /// full batch per QH.
    /// </summary>
    public async ValueTask PublishClearingResultAsync(ClearingResultDto payload, CancellationToken ct = default)
    {
        var envelope = new Envelope<object>(
            MessageType: MessageTypes.AuctionClearingResult,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: null,
            ClientId: AuctionRabbitTopology.ClientId,
            InstrumentId: payload.QuarterId,
            Sequence: null,
            Payload: payload);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));

        await _directChannel.BasicPublishAsync(
            exchange: AuctionRabbitTopology.AuctionExchange,
            routingKey: AuctionRabbitTopology.AuctionClearedRoutingKey(payload.QuarterId),
            mandatory: false,
            basicProperties: new BasicProperties { ContentType = "application/json" },
            body: body,
            cancellationToken: ct);
    }

    /// <summary>
    /// Publishes an <c>auction_bid</c> audit event on
    /// <c>bifrost.public</c>. Fire-and-forget via
    /// <see cref="BufferedEventPublisher"/> — non-blocking.
    /// </summary>
    public void PublishAuctionBidEvent(BidMatrixDto bid)
    {
        _ = _events.PublishPublicEvent(
            AuctionRabbitTopology.EventsRoutingKeyAuctionBid,
            MessageTypes.AuctionBidSubmitted,
            bid);
    }

    /// <summary>
    /// Publishes an <c>auction_cleared</c> audit event on
    /// <c>bifrost.public</c>. Fire-and-forget. The payload is the same
    /// <see cref="ClearingResultDto"/> that the direct clearing path emitted,
    /// which lets the recorder reconstruct the batch from the audit stream.
    /// </summary>
    public void PublishAuctionClearedEvent(ClearingResultDto summary)
    {
        _ = _events.PublishPublicEvent(
            AuctionRabbitTopology.EventsRoutingKeyAuctionCleared,
            MessageTypes.AuctionClearingResult,
            summary);
    }

    /// <summary>
    /// Publishes an <c>auction_no_cross</c> audit event on
    /// <c>bifrost.public</c>. Fire-and-forget. Payload is a minimal object
    /// carrying only the failing <paramref name="quarterId"/>; the recorder's
    /// <c>events</c> table stores the serialised JSON as <c>payload_json</c>.
    /// </summary>
    public void PublishAuctionNoCrossEvent(string quarterId)
    {
        _ = _events.PublishPublicEvent(
            AuctionRabbitTopology.EventsRoutingKeyAuctionNoCross,
            MessageTypes.AuctionNoCross,
            new { quarterId });
    }
}
