using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Exchange.Application;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Bifrost.Exchange.Infrastructure.RabbitMq;

public sealed class CommandConsumerService(
    IConnection connection,
    ExchangeService exchangeService,
    IClock clock,
    ILogger<CommandConsumerService> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await RabbitMqTopology.DeclareExchangeTopologyAsync(_channel, stoppingToken);

        logger.LogInformation("Exchange command consumer started on queue {Queue} (poll mode)", RabbitMqTopology.CommandQueue);

        var messageCount = 0;
        var lastLogCount = 0;
        var lastLogTime = clock.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _channel.BasicGetAsync(RabbitMqTopology.CommandQueue, autoAck: false, stoppingToken);
                if (result is null)
                {
                    await Task.Delay(1, stoppingToken);
                    continue;
                }

                ProcessMessage(result.RoutingKey, result.Body,
                    result.BasicProperties.ReplyTo,
                    result.BasicProperties.MessageId ?? result.BasicProperties.CorrelationId);

                messageCount++;
                await _channel.BasicAckAsync(result.DeliveryTag, false, stoppingToken);

                var now = clock.GetUtcNow();
                if ((now - lastLogTime).TotalSeconds >= 30)
                {
                    var delta = messageCount - lastLogCount;
                    logger.LogInformation(
                        "CONSUMER-HEARTBEAT: processed={Count} delta={Delta} channelOpen={Open}",
                        messageCount, delta, _channel.IsOpen);
                    lastLogCount = messageCount;
                    lastLogTime = now;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in poll loop");
                await Task.Delay(100, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_channel is not null)
        {
            await _channel.CloseAsync(cancellationToken);
            _channel = null;
        }
    }

    private void ProcessMessage(string routingKey, ReadOnlyMemory<byte> bodyBytes,
        string? replyTo, string? correlationId)
    {
        var effectiveCorrelationId = correlationId ?? Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = effectiveCorrelationId });

        using var activity = ExchangeActivitySource.Source.StartActivity(
            "exchange.match-order",
            ActivityKind.Internal);
        activity?.SetTag("bifrost.routing_key", routingKey);
        activity?.SetTag("bifrost.correlation_id", effectiveCorrelationId);

        var body = Encoding.UTF8.GetString(bodyBytes.Span);

        switch (routingKey)
        {
            case RabbitMqTopology.RoutingKeyOrderSubmit:
                var submit = JsonSerializer.Deserialize<SubmitOrderCommand>(body, JsonOptions);
                if (submit is not null)
                    exchangeService.HandleSubmitOrder(submit, replyTo, correlationId).GetAwaiter().GetResult();
                break;

            case RabbitMqTopology.RoutingKeyOrderCancel:
                var cancel = JsonSerializer.Deserialize<CancelOrderCommand>(body, JsonOptions);
                if (cancel is not null)
                    exchangeService.HandleCancelOrder(cancel, replyTo, correlationId).GetAwaiter().GetResult();
                break;

            case RabbitMqTopology.RoutingKeyOrderReplace:
                var replace = JsonSerializer.Deserialize<ReplaceOrderCommand>(body, JsonOptions);
                if (replace is not null)
                    exchangeService.HandleReplaceOrder(replace, replyTo, correlationId).GetAwaiter().GetResult();
                break;

            case RabbitMqTopology.RoutingKeyInquiryBook:
                var snapshot = JsonSerializer.Deserialize<GetBookSnapshotRequest>(body, JsonOptions);
                if (snapshot is not null)
                    exchangeService.HandleGetBookSnapshot(snapshot, replyTo, correlationId).GetAwaiter().GetResult();
                break;

            case RabbitMqTopology.RoutingKeyClientSubscribe:
                var subscribe = JsonSerializer.Deserialize<SubscribeCommand>(body, JsonOptions);
                if (subscribe is not null)
                    exchangeService.HandleSubscribe(subscribe, replyTo, correlationId).GetAwaiter().GetResult();
                break;
        }
    }
}
