using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Bifrost.Imbalance.HostedServices;

/// <summary>
/// Subscribes to the private-exec wildcard on the <c>bifrost.private</c> topic
/// exchange and feeds <see cref="FillMessage"/> instances into the simulator's
/// shared channel for (clientId, quarterIndex) net-position accumulation.
/// <para>
/// Queue shape: exclusive + auto-delete, non-durable. The RabbitMQ 4 broker
/// rejects transient non-exclusive queues by default (the
/// <c>transient_nonexcl_queues</c> deprecation flag is a hard block), so this
/// consumer follows the same exclusive-queue convention as the quoter's private
/// consumer and the recorder's event consumer. There is exactly one imbalance
/// simulator process per compose stack; the queue dies with the connection,
/// which is semantically correct for a single-consumer fan-out sink.
/// </para>
/// <para>
/// Ack discipline: <c>autoAck=false</c>. The consumer calls
/// <see cref="IChannel.BasicAckAsync"/> only AFTER the successful
/// <see cref="ChannelWriter{T}.WriteAsync"/> onto the simulator channel (or
/// after an ack-skip for an hour-instrument fill). Under saturation the channel
/// blocks via <c>FullMode=Wait</c>; the broker then redelivers if the consumer
/// is cancelled before ack — no fill is lost silently. Hour-instrument fills
/// are acked + skipped: <see cref="QuarterIndexResolver.Resolve(InstrumentIdDto)"/>
/// returns null, and they make no A_teams contribution.
/// </para>
/// </summary>
public sealed class FillConsumerHostedService : BackgroundService
{
    private const string QueueName = "bifrost.imbalance.fills";
    private const string RoutingPattern = "private.exec.*.fill";

    private readonly IConnection _connection;
    private readonly Channel<SimulatorMessage> _channel;
    private readonly QuarterIndexResolver _resolver;
    private readonly ILogger<FillConsumerHostedService> _log;
    private IChannel? _consumeChannel;

    public FillConsumerHostedService(
        IConnection connection,
        Channel<SimulatorMessage> channel,
        QuarterIndexResolver resolver,
        ILogger<FillConsumerHostedService> log)
    {
        _connection = connection;
        _channel = channel;
        _resolver = resolver;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumeChannel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declare exchange first (idempotent — the exchange service already
        // publishes here). Topic + durable matches RabbitMqTopology.
        await _consumeChannel.ExchangeDeclareAsync(
            RabbitMqTopology.PrivateExchange, ExchangeType.Topic, durable: true,
            cancellationToken: stoppingToken);

        // Exclusive + auto-delete + non-durable. See class summary for rationale.
        await _consumeChannel.QueueDeclareAsync(
            QueueName, durable: false, exclusive: true, autoDelete: true,
            cancellationToken: stoppingToken);

        // Wildcard binding: matches every team's fill routing key
        // private.exec.<clientId>.fill. Fan-out is a standard RabbitMQ topic
        // behaviour — a fill routed to a team's private queue ALSO routes here
        // because both bindings match.
        await _consumeChannel.QueueBindAsync(
            QueueName, RabbitMqTopology.PrivateExchange, RoutingPattern,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_consumeChannel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                await HandleAsync(ea, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — do not ack; let the broker redeliver when
                // the consumer reconnects.
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Fill-consumer dispatch failure on deliveryTag {DeliveryTag}", ea.DeliveryTag);
                // Ack anyway so the broker does not loop on a poison message —
                // malformed envelopes are logged, not retried indefinitely.
                await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
            }
        };

        await _consumeChannel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer,
            cancellationToken: stoppingToken);

        _log.LogInformation(
            "Fill consumer started: queue {Queue} bound to {Exchange} on {Pattern}",
            QueueName, RabbitMqTopology.PrivateExchange, RoutingPattern);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Decode one delivery, resolve quarter_index, enqueue a FillMessage (or
    /// ack-skip for an hour-instrument fill), then ack. Extracted for
    /// readability and for future unit-test drive-through via
    /// InternalsVisibleTo if one is ever needed.
    /// </summary>
    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize(
            ea.Body.Span,
            ImbalanceJsonContext.Default.EnvelopeOrderExecutedEvent);

        if (envelope?.Payload is not { } fill)
        {
            // Unparseable or empty payload — ack + drop.
            await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, ct);
            return;
        }

        var quarterIndex = _resolver.Resolve(fill.InstrumentId);
        if (quarterIndex is null)
        {
            // Hour-instrument fill — no A_teams contribution. Ack + skip.
            await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, ct);
            return;
        }

        // Buy = +qty, Sell = -qty. Quantity is decimal MWh on the wire; the
        // simulator accumulates signed quantity in ticks via TicksPerEuro
        // (matches the convention in ImbalancePricingEngine for the arithmetic
        // at Gate). A dedicated ticks_per_mwh factor would be cleaner long-term
        // but a single conversion factor is sufficient for integer equality
        // through the Gate math.
        var sign = string.Equals(fill.Side, "Buy", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        var qtyTicks = (long)(sign * fill.FilledQuantity * 100m);

        var instrumentIdLabel = FormatInstrument(fill.InstrumentId);

        await _channel.Writer.WriteAsync(
            new FillMessage(
                TsNs: fill.TimestampNs,
                ClientId: fill.ClientId,
                InstrumentId: instrumentIdLabel,
                QuarterIndex: quarterIndex.Value,
                Side: fill.Side,
                QuantityTicks: qtyTicks),
            ct);

        // Ack AFTER successful WriteAsync so the broker redelivers under
        // back-pressure cancellation.
        await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, ct);
    }

    /// <summary>
    /// Canonical instrument-id label matching the recorder's format
    /// (<c>{DeliveryArea}-{Start:yyyyMMddTHHmm}-{End:yyyyMMddTHHmm}</c>). The
    /// simulator does not parse this label — <see cref="QuarterIndexResolver"/>
    /// operates on the DTO directly — but carrying a human-readable label
    /// through FillMessage makes log traces and recorded rows self-describing.
    /// </summary>
    private static string FormatInstrument(InstrumentIdDto id) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{id.DeliveryArea}-{id.DeliveryPeriodStart:yyyyMMddTHHmm}-{id.DeliveryPeriodEnd:yyyyMMddTHHmm}");

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_consumeChannel is not null)
        {
            try
            {
                await _consumeChannel.CloseAsync(cancellationToken);
            }
            catch
            {
                // Best-effort cleanup — channel may already be closing.
            }
            _consumeChannel.Dispose();
            _consumeChannel = null;
        }

        await base.StopAsync(cancellationToken);
    }
}
