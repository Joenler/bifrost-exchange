using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Quoter.Schedule;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Bifrost.Quoter.Rabbit;

/// <summary>
/// Poll-mode <see cref="BackgroundService"/> consumer for inbound MC regime-force
/// commands published by the orchestrator on <see cref="QuoterRabbitTopology.McRegimeQueue"/>.
/// Shape-matches <c>Bifrost.Exchange.Infrastructure.RabbitMq.CommandConsumerService</c>:
/// per-service channel, <c>BasicGetAsync</c> poll loop, structured logging, soft
/// retry on parse failures.
///
/// Nonce idempotency is enforced downstream in
/// <c>RegimeSchedule.InstallMcForce</c>'s <c>LruSet</c> -- this consumer simply
/// forwards every successfully-parsed message into the inbox channel.
/// </summary>
public sealed class McRegimeForceConsumer(
    IConnection connection,
    Channel<RegimeForceMessage> inbox,
    ILogger<McRegimeForceConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declare the MC fanout exchange + queue + binding. Both ends are safe
        // to declare idempotently (RabbitMQ.Client semantics); whichever side
        // boots first wins the declaration race.
        await _channel.ExchangeDeclareAsync(
            QuoterRabbitTopology.McRegimeExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await _channel.QueueDeclareAsync(
            QuoterRabbitTopology.McRegimeQueue,
            durable: false,
            exclusive: false,
            autoDelete: true,
            cancellationToken: stoppingToken);

        await _channel.QueueBindAsync(
            QuoterRabbitTopology.McRegimeQueue,
            QuoterRabbitTopology.McRegimeExchange,
            QuoterRabbitTopology.McRegimeRoutingKey,
            cancellationToken: stoppingToken);

        logger.LogInformation(
            "MC regime-force consumer started on queue {Queue} (poll mode)",
            QuoterRabbitTopology.McRegimeQueue);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _channel.BasicGetAsync(
                    QuoterRabbitTopology.McRegimeQueue,
                    autoAck: true,
                    stoppingToken);

                if (result is null)
                {
                    await Task.Delay(50, stoppingToken);
                    continue;
                }

                McRegimeForceDto? dto;
                try
                {
                    dto = JsonSerializer.Deserialize<McRegimeForceDto>(result.Body.Span, JsonOptions);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Dropping malformed MC regime-force payload");
                    continue;
                }

                if (dto is null)
                {
                    continue;
                }

                await inbox.Writer.WriteAsync(
                    new RegimeForceMessage(dto.Regime, dto.Nonce),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in MC regime-force poll loop");
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
}
