using System.Text;
using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application;
using Bifrost.Time;
using RabbitMQ.Client;

namespace Bifrost.Exchange.Infrastructure.RabbitMq;

public sealed class RabbitMqEventPublisher(IChannel channel, IClock clock) : IEventPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask PublishPrivate(string clientId, object @event, string? correlationId = null)
    {
        var (routingKey, messageType) = ResolvePrivateRouting(clientId, @event);
        var envelope = new Envelope<object>(messageType, clock.GetUtcNow(),
            correlationId, clientId, null, null, @event);
        var body = Serialize(envelope);

        await channel.BasicPublishAsync(
            RabbitMqTopology.PrivateExchange,
            routingKey,
            false,
            new BasicProperties { ContentType = "application/json" },
            body);
    }

    public async ValueTask PublishPublicTrade(string instrumentId, object trade, long sequence)
    {
        var routingKey = RabbitMqTopology.PublicTradeRoutingKey(instrumentId);
        var envelope = new Envelope<object>(MessageTypes.PublicTrade, clock.GetUtcNow(),
            null, null, instrumentId, sequence, trade);
        var body = Serialize(envelope);

        await channel.BasicPublishAsync(
            RabbitMqTopology.PublicExchange,
            routingKey,
            false,
            new BasicProperties { ContentType = "application/json" },
            body);
    }

    public async ValueTask PublishPublicDelta(string instrumentId, object delta, long sequence)
    {
        var routingKey = RabbitMqTopology.PublicDeltaRoutingKey(instrumentId);
        var envelope = new Envelope<object>(MessageTypes.BookDelta, clock.GetUtcNow(),
            null, null, instrumentId, sequence, delta);
        var body = Serialize(envelope);

        await channel.BasicPublishAsync(
            RabbitMqTopology.PublicExchange,
            routingKey,
            false,
            new BasicProperties { ContentType = "application/json" },
            body);
    }

    public async ValueTask PublishReply(string replyTo, string correlationId, object response)
    {
        var (_, messageType) = ResolvePrivateRouting("", response);
        var envelope = new Envelope<object>(messageType, clock.GetUtcNow(),
            correlationId, null, null, null, response);
        var body = Serialize(envelope);
        var props = new BasicProperties
        {
            CorrelationId = correlationId,
            ContentType = "application/json"
        };

        await channel.BasicPublishAsync(
            "",
            replyTo,
            false,
            props,
            body);
    }

    public async ValueTask PublishPublicOrderStats(string instrumentId, object stats)
    {
        var routingKey = $"public.orderstats.{instrumentId}";
        var envelope = new Envelope<object>(MessageTypes.PublicOrderStats, clock.GetUtcNow(),
            null, null, instrumentId, null, stats);
        var body = Serialize(envelope);

        await channel.BasicPublishAsync(
            RabbitMqTopology.PublicExchange,
            routingKey,
            false,
            new BasicProperties { ContentType = "application/json" },
            body);
    }

    public async ValueTask PublishPublicSnapshot(string instrumentId, object snapshot, long sequence)
    {
        var routingKey = RabbitMqTopology.PublicSnapshotRoutingKey(instrumentId);
        var envelope = new Envelope<object>(MessageTypes.BookSnapshot, clock.GetUtcNow(),
            null, null, instrumentId, sequence, snapshot);
        var body = Serialize(envelope);

        await channel.BasicPublishAsync(
            RabbitMqTopology.PublicExchange,
            routingKey,
            false,
            new BasicProperties { ContentType = "application/json" },
            body);
    }

    public async ValueTask PublishPublicInstrument(object @event)
    {
        var envelope = new Envelope<object>(MessageTypes.InstrumentAvailable, clock.GetUtcNow(),
            null, null, null, null, @event);
        var body = Serialize(envelope);

        await channel.BasicPublishAsync(
            RabbitMqTopology.PublicExchange,
            RabbitMqTopology.PublicInstrumentAvailableRoutingKey,
            false,
            new BasicProperties { ContentType = "application/json" },
            body);
    }

    /// <summary>
    /// Publishes a generic public-events payload (events.proto::Event oneof
    /// variants such as RegimeChange, ForecastRevision, etc.) onto
    /// <see cref="RabbitMqTopology.PublicExchange"/> with the caller-supplied
    /// routing key and message-type discriminator. Quoter / orchestrator /
    /// imbalance-simulator consumers all flow through here so the wire shape
    /// (envelope + payload bytes) stays consistent.
    /// </summary>
    public async ValueTask PublishPublicEvent(string routingKey, string messageType, object @event)
    {
        var envelope = new Envelope<object>(messageType, clock.GetUtcNow(),
            null, null, null, null, @event);
        var body = Serialize(envelope);

        await channel.BasicPublishAsync(
            RabbitMqTopology.PublicExchange,
            routingKey,
            false,
            new BasicProperties { ContentType = "application/json" },
            body);
    }

    private static (string RoutingKey, string MessageType) ResolvePrivateRouting(string clientId, object @event)
    {
        return @event switch
        {
            OrderAcceptedEvent => (RabbitMqTopology.PrivateOrderRoutingKey(clientId, "accepted"), MessageTypes.OrderAccepted),
            OrderRejectedEvent => (RabbitMqTopology.PrivateOrderRoutingKey(clientId, "rejected"), MessageTypes.OrderRejected),
            OrderCancelledEvent => (RabbitMqTopology.PrivateOrderRoutingKey(clientId, "cancelled"), MessageTypes.OrderCancelled),
            OrderExecutedEvent => (RabbitMqTopology.PrivateExecRoutingKey(clientId, "fill"), MessageTypes.OrderExecuted),
            MarketOrderRemainderCancelledEvent => (RabbitMqTopology.PrivateExecRoutingKey(clientId, "done"), MessageTypes.MarketOrderRemainderCancelled),
            ExchangeMetadataEvent => ($"private.order.{clientId}.metadata", MessageTypes.ExchangeMetadata),
            InstrumentListEvent => ($"private.order.{clientId}.instruments", MessageTypes.InstrumentList),
            BookSnapshotResponse => ($"private.inquiry.{clientId}.book", MessageTypes.BookSnapshot),
            _ => throw new InvalidOperationException($"No message type mapping for {@event.GetType().Name}. Add a case to ResolvePrivateRouting and a constant to MessageTypes.")
        };
    }

    private static ReadOnlyMemory<byte> Serialize(Envelope<object> envelope)
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    public async ValueTask DisposeAsync()
    {
        await channel.CloseAsync();
    }
}
