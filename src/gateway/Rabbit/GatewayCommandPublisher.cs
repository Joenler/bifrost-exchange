using System.Text;
using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Time;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Dedicated-IChannel publisher for inbound team commands translated to internal DTOs
/// (SubmitOrderCommand / CancelOrderCommand / ReplaceOrderCommand). Pitfall 6:
/// RabbitMQ.Client 7.x channels are NOT thread-safe — DI factory creates exactly one
/// channel for this publisher instance, and the channel is never shared with any
/// consumer or other publisher.
///
/// Wire shape mirrors <c>RabbitMqEventPublisher</c> verbatim:
///   - System.Text.Json with camelCase property names,
///   - <see cref="Envelope{T}"/> wrap with the matching <see cref="MessageTypes"/> discriminator,
///   - <c>BasicProperties { ContentType = "application/json", CorrelationId = correlationId }</c>.
///
/// Routes commands to <see cref="RabbitMqTopology.CommandExchange"/> on the
/// <c>cmd.order.{submit|cancel|replace}</c> routing keys — same surface the
/// matching engine's <c>CommandConsumerService</c> already consumes.
///
/// Each publish method contains its own <c>BasicPublishAsync</c> call (per
/// 07-05-PLAN.md acceptance fence) so a future surgical change to a single
/// command's wire properties stays localised to that method.
/// </summary>
public sealed class GatewayCommandPublisher : IGatewayCommandPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IChannel _channel;
    private readonly IClock _clock;
    private readonly ILogger<GatewayCommandPublisher> _log;

    public GatewayCommandPublisher(IChannel channel, IClock clock, ILogger<GatewayCommandPublisher> log)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async ValueTask PublishSubmitOrderAsync(string clientId, SubmitOrderCommand cmd, string correlationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var envelope = new Envelope<object>(
            MessageType: MessageTypes.SubmitOrder,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: correlationId,
            ClientId: clientId,
            InstrumentId: null,
            Sequence: null,
            Payload: cmd);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
        var props = new BasicProperties { ContentType = "application/json", CorrelationId = correlationId };

        await _channel.BasicPublishAsync(
            exchange: RabbitMqTopology.CommandExchange,
            routingKey: RabbitMqTopology.RoutingKeyOrderSubmit,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);
    }

    public async ValueTask PublishCancelOrderAsync(string clientId, CancelOrderCommand cmd, string correlationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var envelope = new Envelope<object>(
            MessageType: MessageTypes.CancelOrder,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: correlationId,
            ClientId: clientId,
            InstrumentId: null,
            Sequence: null,
            Payload: cmd);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
        var props = new BasicProperties { ContentType = "application/json", CorrelationId = correlationId };

        await _channel.BasicPublishAsync(
            exchange: RabbitMqTopology.CommandExchange,
            routingKey: RabbitMqTopology.RoutingKeyOrderCancel,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);
    }

    public async ValueTask PublishReplaceOrderAsync(string clientId, ReplaceOrderCommand cmd, string correlationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var envelope = new Envelope<object>(
            MessageType: MessageTypes.ReplaceOrder,
            TimestampUtc: _clock.GetUtcNow(),
            CorrelationId: correlationId,
            ClientId: clientId,
            InstrumentId: null,
            Sequence: null,
            Payload: cmd);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
        var props = new BasicProperties { ContentType = "application/json", CorrelationId = correlationId };

        await _channel.BasicPublishAsync(
            exchange: RabbitMqTopology.CommandExchange,
            routingKey: RabbitMqTopology.RoutingKeyOrderReplace,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _channel.CloseAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GatewayCommandPublisher channel close failed");
        }
        _channel.Dispose();
    }
}
