using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Translation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Auction-result fan-out consumer. <see cref="AsyncEventingBasicConsumer"/>
/// push pattern (Pitfall 9 — NEVER <c>BasicGetAsync</c> poll). Owns its OWN
/// <see cref="IChannel"/> from the shared <see cref="IConnection"/> (Pitfall 6).
///
/// Binds <see cref="GatewayTopology.AuctionExchange"/> (the gateway's local
/// mirror of <c>Bifrost.DahAuction.Rabbit.AuctionRabbitTopology.AuctionExchange</c>
/// — same value, no Web SDK project reference). Per Phase 05 D-09:
/// <see cref="ClearingResultDto.TeamName"/> == null → broadcast public summary
/// row; non-null → enqueue ONLY to that team via
/// <see cref="TeamRegistry.TryGetByName"/>.
///
/// Pitfall 10: ring-Append + lock release → outbound write.
/// </summary>
public sealed class AuctionResultConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConnection _connection;
    private readonly TeamRegistry _registry;
    private readonly ILogger<AuctionResultConsumer> _log;
    private IChannel? _channel;

    public AuctionResultConsumer(
        IConnection connection,
        TeamRegistry registry,
        ILogger<AuctionResultConsumer> log)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pitfall 6: dedicated channel per consumer.
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // bifrost.auction is a Direct exchange (per AuctionRabbitTopology) but
        // the gateway can still bind a per-team queue with a routing-key prefix.
        await _channel.ExchangeDeclareAsync(
            GatewayTopology.AuctionExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await _channel.QueueDeclareAsync(
            GatewayTopology.AuctionResultQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken);

        // Direct exchange — bind once per quarter id we care about. For Phase 07
        // the canonical 4 quarter ids match the per-publish routing key
        // "bifrost.auction.cleared.{quarterId}" in AuctionPublisher; the
        // gateway binds the 4 known quarter ids deterministically.
        foreach (var quarterId in new[] { "Q1", "Q2", "Q3", "Q4" })
        {
            await _channel.QueueBindAsync(
                GatewayTopology.AuctionResultQueue,
                GatewayTopology.AuctionExchange,
                GatewayTopology.AuctionClearedRoutingKey(quarterId),
                cancellationToken: stoppingToken);
        }

        // PUSH, not poll — Pitfall 9.
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                await HandleDeliveryAsync(ea, stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "Auction-result delivery failed");
            }
        };
        await _channel.BasicConsumeAsync(
            GatewayTopology.AuctionResultQueue,
            autoAck: true,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _log.LogInformation("Auction-result consumer started on queue {Queue} (push mode)", GatewayTopology.AuctionResultQueue);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }

    private async Task HandleDeliveryAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize<Envelope<JsonElement>>(ea.Body.Span, JsonOptions);
        if (envelope is null) return;
        if (envelope.MessageType != MessageTypes.AuctionClearingResult) return;

        var clearing = envelope.Payload.Deserialize<ClearingResultDto>(JsonOptions);
        if (clearing is null) return;

        var marketEvent = OutboundTranslator.FromAuctionClearingResult(envelope);

        if (string.IsNullOrEmpty(clearing.TeamName))
        {
            // Phase 05 D-09: TeamName == null → broadcast public summary to every team.
            var teams = _registry.SnapshotAll();
            for (var i = 0; i < teams.Length; i++)
            {
                await PublishToTeamAsync(teams[i], envelope, marketEvent, ct);
            }
        }
        else
        {
            // Per-team award row: enqueue only to that team.
            if (_registry.TryGetByName(clearing.TeamName, out var teamState) && teamState is not null)
            {
                await PublishToTeamAsync(teamState, envelope, marketEvent, ct);
            }
        }
    }

    private static async Task PublishToTeamAsync(
        TeamState teamState,
        Envelope<JsonElement> envelope,
        StrategyProto.MarketEvent marketEvent,
        CancellationToken ct)
    {
        lock (teamState.StateLock)
        {
            var wrapper = new Envelope<object>(
                MessageType: envelope.MessageType,
                TimestampUtc: envelope.TimestampUtc,
                CorrelationId: envelope.CorrelationId,
                ClientId: teamState.ClientId,
                InstrumentId: envelope.InstrumentId,
                Sequence: null,
                Payload: marketEvent);
            teamState.Ring.Append(wrapper);
        }
        // Pitfall 10: write outside the lock.
        if (teamState.Outbound is { } writer)
        {
            await writer.WriteAsync(marketEvent, ct);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            try
            {
                await _channel.CloseAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "AuctionResultConsumer channel close failed");
            }
            // Dispose can NRE on RabbitMQ.Client 7.x channels whose underlying
            // session was already torn down by the connection. Swallow.
            try { _channel.Dispose(); }
            catch (Exception ex) { _log.LogWarning(ex, "AuctionResultConsumer channel dispose failed"); }
            _channel = null;
        }
        await base.StopAsync(cancellationToken);
    }
}
