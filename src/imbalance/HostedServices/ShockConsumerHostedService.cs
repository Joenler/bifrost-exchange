using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Bifrost.Imbalance.HostedServices;

/// <summary>
/// Subscribes to MC-injected <see cref="PhysicalShockEvent"/>s on the public
/// topic exchange and feeds <see cref="ShockMessage"/> instances into the
/// simulator's shared channel for A_physical per-QH accumulation.
/// <para>
/// Phase-ownership: the orchestrator that publishes these events is a later
/// phase (the MC console + round orchestrator). The routing key pattern
/// <c>events.physical.shock</c> is forward-compatible — the binding ships now
/// so no topology rewiring is needed when the publisher lands; until then, the
/// queue receives nothing in production. Phase 04 integration tests inject
/// <see cref="ShockMessage"/> directly onto the shared channel rather than
/// round-tripping through RabbitMQ.
/// </para>
/// <para>
/// Queue shape matches the sibling fill consumer: exclusive + auto-delete +
/// non-durable. RabbitMQ 4 rejects transient non-exclusive queues by default
/// (the <c>transient_nonexcl_queues</c> deprecation flag is a hard block), and
/// there is exactly one imbalance simulator process per compose stack — so the
/// queue dying with the connection is semantically correct.
/// </para>
/// <para>
/// D-09 defense-in-depth: the primary invariant (every physical-shock event
/// carries a valid quarter index 0..3) is enforced at the orchestrator
/// boundary — the simulator treats that as a load-bearing contract. This
/// consumer reasserts the invariant at the wire boundary as a second line of
/// defence: any shock with an out-of-range <c>QuarterIndex</c> is logged at
/// Error and dropped (ack + skip) rather than enqueued. The drain-loop
/// <c>HandleShock</c> arm carries a matching <see cref="System.Diagnostics.Debug.Assert"/>
/// plus release-mode range guard so a regression on either side surfaces
/// loudly rather than silently corrupting A_physical.
/// </para>
/// <para>
/// Ack discipline: <c>autoAck=false</c>. <see cref="IChannel.BasicAckAsync"/>
/// is called only AFTER the successful <see cref="ChannelWriter{T}.WriteAsync"/>
/// onto the simulator channel — or after the defensive-drop ack-skip. Under
/// saturation the channel blocks via <c>FullMode=Wait</c>; if the consumer is
/// cancelled before ack, the broker redelivers so no valid shock is lost.
/// Malformed envelopes are logged at Error and acked (poison-message policy
/// matches the recorder + fill consumer conventions).
/// </para>
/// </summary>
public sealed class ShockConsumerHostedService : BackgroundService
{
    private const string QueueName = "bifrost.imbalance.shocks";

    // The event-bus routing pattern is owned by the future orchestrator phase
    // that publishes physical-shock events. The binding ships now with this
    // tentative pattern so the wire is ready on the simulator side; when the
    // publisher lands it will use the same pattern or the orchestrator phase
    // will coordinate the rename. Until then, the queue receives nothing in
    // production — which is exactly the right behaviour.
    private const string RoutingPattern = "events.physical.shock";

    private readonly IConnection _connection;
    private readonly Channel<SimulatorMessage> _channel;
    private readonly ILogger<ShockConsumerHostedService> _log;
    private IChannel? _consumeChannel;

    public ShockConsumerHostedService(
        IConnection connection,
        Channel<SimulatorMessage> channel,
        ILogger<ShockConsumerHostedService> log)
    {
        _connection = connection;
        _channel = channel;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumeChannel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declare the public exchange idempotently. Topic + durable matches
        // RabbitMqTopology; the exchange service will already have declared
        // the same shape, so this is pure safety in case the consumer starts
        // before any publisher has touched the broker.
        await _consumeChannel.ExchangeDeclareAsync(
            RabbitMqTopology.PublicExchange, ExchangeType.Topic, durable: true,
            cancellationToken: stoppingToken);

        // Exclusive + auto-delete + non-durable. See class summary for rationale.
        await _consumeChannel.QueueDeclareAsync(
            QueueName, durable: false, exclusive: true, autoDelete: true,
            cancellationToken: stoppingToken);

        await _consumeChannel.QueueBindAsync(
            QueueName, RabbitMqTopology.PublicExchange, RoutingPattern,
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
                _log.LogError(ex, "Shock-consumer dispatch failure on deliveryTag {DeliveryTag}", ea.DeliveryTag);
                // Ack anyway so the broker does not loop on a poison message —
                // malformed envelopes are logged, not retried indefinitely.
                await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
            }
        };

        await _consumeChannel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer,
            cancellationToken: stoppingToken);

        _log.LogInformation(
            "Shock consumer started: queue {Queue} bound to {Exchange} on {Pattern}",
            QueueName, RabbitMqTopology.PublicExchange, RoutingPattern);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Decode one delivery, validate quarter_index defensively, enqueue a
    /// <see cref="ShockMessage"/>, then ack. Extracted for readability and so a
    /// future unit-test driver via <c>InternalsVisibleTo</c> can exercise the
    /// dispatch branch without a live broker.
    /// </summary>
    private async Task HandleAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var envelope = JsonSerializer.Deserialize(
            ea.Body.Span,
            ImbalanceJsonContext.Default.EnvelopePhysicalShockEvent);

        if (envelope?.Payload is not { } shock)
        {
            // Unparseable or empty payload — ack + drop.
            await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, ct);
            return;
        }

        // D-09 defense-in-depth: the orchestrator is contractually required to
        // reject any PhysicalShockCmd with an unset or out-of-range
        // quarter_index at its boundary. The simulator reasserts here so a
        // regression upstream surfaces at a loud log line rather than silently
        // skewing a single quarter's A_physical accumulator.
        if (shock.QuarterIndex is < 0 or > 3)
        {
            _log.LogError(
                "Dropping PhysicalShock with out-of-range QuarterIndex={Qh} label={Label} — upstream contract violation",
                shock.QuarterIndex, shock.Label);
            await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, ct);
            return;
        }

        var persistence = shock.Persistence.Equals("Transient", StringComparison.OrdinalIgnoreCase)
            ? ShockPersistence.Transient
            : ShockPersistence.Round;

        await _channel.Writer.WriteAsync(
            new ShockMessage(
                TsNs: shock.TimestampNs,
                Mw: shock.Mw,
                Label: shock.Label,
                Persistence: persistence,
                QuarterIndex: shock.QuarterIndex),
            ct);

        // Ack AFTER successful WriteAsync so the broker redelivers under
        // back-pressure cancellation.
        await _consumeChannel!.BasicAckAsync(ea.DeliveryTag, false, ct);
    }

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
