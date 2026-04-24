using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Recorder.Session;
using Bifrost.Recorder.Storage;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Bifrost.Recorder.Infrastructure;

/// <summary>
/// Drains the BIFROST events exchange into <see cref="Channel{WriteCommand}"/>.
/// Dispatch table is the BIFROST split-event shape: <see cref="MessageTypes"/>
/// values fan out to <see cref="OrderWrite"/> / <see cref="FillWrite"/> /
/// <see cref="RejectWrite"/> / <see cref="BookUpdateWrite"/> /
/// <see cref="TradeWrite"/> / <see cref="EventWrite"/>.
/// </summary>
/// <remarks>
/// <para>Arena dispatched three message types (TraderOrderEvent, TraderLifecycleEvent,
/// TraderMetrics) through a single <c>order_events</c> / <c>lifecycle_events</c>
/// pair of tables. BIFROST carries split tables (orders, fills, rejects,
/// book_updates, trades, events); the dispatch switch writes one envelope to
/// one subtype with explicit table-targeting semantics.</para>
///
/// <para><b>MarketOrderRemainderCancelled</b> is LOCKED to
/// <c>OrderWrite(action="cancel_remainder")</c>: the event is a lifecycle
/// signal on an order that matched what it could and had its unfilled
/// remainder cancelled by the exchange for insufficient liquidity. It is
/// NOT a pre-match refusal, so it lands in the <c>orders</c> table (not
/// <c>rejects</c>). The action verb <c>cancel_remainder</c> disambiguates
/// from a team-initiated <c>cancel</c>.</para>
/// </remarks>
public sealed class RabbitMqRecorderConsumer : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly Channel<WriteCommand> _channel;
    private readonly ChannelWriter<WriteCommand> _channelWriter;
    private readonly SessionManager _sessionManager;
    private readonly SessionIndex _sessionIndex;
    private readonly ExitReasonDetector _exitDetector;
    private readonly Manifest _manifest;
    private readonly string _sessionDir;
    private readonly SessionDatabase _db;
    private readonly RecorderMetrics _metrics;
    private readonly IClock _clock;
    private readonly ILogger<RabbitMqRecorderConsumer> _logger;
    private readonly int _channelCapacity;

    private readonly Stopwatch _dropStopwatch = new();
    private bool _isDegraded;

    private IConnection? _connection;
    private IChannel? _consumeChannel;

    public bool IsDegraded => _isDegraded;

    public RabbitMqRecorderConsumer(
        IConfiguration configuration,
        Channel<WriteCommand> channel,
        SessionManager sessionManager,
        SessionIndex sessionIndex,
        ExitReasonDetector exitDetector,
        Manifest manifest,
        string sessionDir,
        SessionDatabase db,
        RecorderMetrics metrics,
        IClock clock,
        ILogger<RabbitMqRecorderConsumer> logger,
        int channelCapacity = 10_000)
    {
        _configuration = configuration;
        _channel = channel;
        _channelWriter = channel.Writer;
        _sessionManager = sessionManager;
        _sessionIndex = sessionIndex;
        _exitDetector = exitDetector;
        _manifest = manifest;
        _sessionDir = sessionDir;
        _db = db;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
        _channelCapacity = channelCapacity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _configuration["RabbitMq:Host"] ?? "localhost",
            Port = int.TryParse(_configuration["RabbitMq:Port"], out var p) ? p : 5672,
            UserName = _configuration["RabbitMq:Username"] ?? "guest",
            Password = _configuration["RabbitMq:Password"] ?? "guest",
        };

        var pipeline = RabbitMqResilience.CreateConnectionPipeline(_logger);
        _connection = await pipeline.ExecuteAsync(
            async ct => await factory.CreateConnectionAsync("bifrost-recorder", ct),
            stoppingToken);
        _logger.LogInformation("Connected to RabbitMQ");

        if (_connection is null) return;

        _consumeChannel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await _consumeChannel.ExchangeDeclareAsync(
            RecorderTopology.TraderEventsExchange, ExchangeType.Topic, durable: true,
            cancellationToken: stoppingToken);

        // RabbitMQ 4 rejects transient non-exclusive queues
        // (`transient_nonexcl_queues` deprecation flag is a hard block by default).
        // The recorder is a single-process consumer per compose stack, so exclusive
        // (queue tied to this connection, auto-deleted on drop) is semantically
        // correct and unblocks the deprecation.
        await _consumeChannel.QueueDeclareAsync(
            RecorderTopology.RecorderEventsQueue, durable: false, exclusive: true, autoDelete: true,
            cancellationToken: stoppingToken);
        await _consumeChannel.QueueBindAsync(
            RecorderTopology.RecorderEventsQueue, RecorderTopology.TraderEventsExchange,
            RecorderTopology.OrderRoutingKey, cancellationToken: stoppingToken);
        await _consumeChannel.QueueBindAsync(
            RecorderTopology.RecorderEventsQueue, RecorderTopology.TraderEventsExchange,
            RecorderTopology.LifecycleRoutingKey, cancellationToken: stoppingToken);

        // Second exchange binding: imbalance-simulator publishes per-team
        // ImbalanceSettlement envelopes to bifrost.private with routing key
        // private.imbalance.settlement.<clientId>. The recorder reuses the
        // existing RecorderEventsQueue (single-queue, multi-binding) to
        // capture them alongside the order/lifecycle stream.
        await _consumeChannel.ExchangeDeclareAsync(
            RecorderTopology.PrivateExchange, ExchangeType.Topic, durable: true,
            cancellationToken: stoppingToken);
        await _consumeChannel.QueueBindAsync(
            RecorderTopology.RecorderEventsQueue, RecorderTopology.PrivateExchange,
            RecorderTopology.ImbalanceSettlementRoutingKey,
            cancellationToken: stoppingToken);

        // Third exchange binding: public audit events from the bifrost.public
        // exchange. The public exchange is declared by the central exchange
        // service on its own boot; we only bind our queue to it here. The
        // events.# pattern catches every events.* audit-event key — the
        // auction service's events.auction.bid / events.auction.cleared /
        // events.auction.no_cross rows, the quoter's events.regime.change
        // rows, and any future public audit publishers — all multiplexed
        // into the existing events table without schema change.
        await _consumeChannel.QueueBindAsync(
            RecorderTopology.RecorderEventsQueue,
            RecorderTopology.PublicEventsExchange,
            RecorderTopology.PublicEventsRoutingKey,
            cancellationToken: stoppingToken);

        var eventsConsumer = new AsyncEventingBasicConsumer(_consumeChannel);
        eventsConsumer.ReceivedAsync += async (_, ea) =>
        {
            var correlationId = ea.BasicProperties.MessageId
                ?? ea.BasicProperties.CorrelationId
                ?? Guid.NewGuid().ToString("N");
            using var scope = _logger.BeginScope(
                new Dictionary<string, object> { ["CorrelationId"] = correlationId });
            try
            {
                DispatchMessage(ea.Body.Span);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching recorder event message");
            }

            await _consumeChannel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
        };
        await _consumeChannel.BasicConsumeAsync(
            RecorderTopology.RecorderEventsQueue, autoAck: false, consumer: eventsConsumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Recorder RabbitMQ consumer started, bound to {EventsQueue}",
            RecorderTopology.RecorderEventsQueue);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Dispatch one RabbitMQ body into <see cref="_channelWriter"/>. Exposed
    /// via <c>InternalsVisibleTo("Bifrost.Recorder.Tests")</c> so the Wave 0
    /// persistence test can drive the dispatch path without a live broker.
    /// </summary>
    /// <remarks>
    /// Dispatch table:
    /// <list type="bullet">
    /// <item>OrderAccepted → OrderWrite(action="submit")</item>
    /// <item>OrderRejected → RejectWrite</item>
    /// <item>OrderCancelled → OrderWrite(action="cancel")</item>
    /// <item>OrderExecuted → FillWrite</item>
    /// <item>MarketOrderRemainderCancelled → OrderWrite(action="cancel_remainder")
    ///   [LOCKED: lifecycle event on the order, NOT a pre-match refusal.
    ///   New action verb introduced in the BIFROST orders table.]</item>
    /// <item>BookDelta → BookUpdateWrite (one per changed level per side)</item>
    /// <item>PublicTrade → TradeWrite</item>
    /// <item>other → EventWrite (news / alerts / round-state / shocks)</item>
    /// </list>
    /// </remarks>
    internal void DispatchMessage(ReadOnlySpan<byte> body)
    {
        const int MaxMessageBytes = 256 * 1024;
        if (body.Length > MaxMessageBytes)
        {
            _metrics.EventsDropped++;
            _logger.LogWarning(
                "Oversized message rejected: {Bytes} bytes (cap {Cap})",
                body.Length, MaxMessageBytes);
            return;
        }

        var envelope = JsonSerializer.Deserialize(body, RecorderJsonContext.Default.EnvelopeJsonElement);
        if (envelope is null) return;

        _exitDetector.OnEventReceived();

        CheckDegradationExit();

        if (_isDegraded)
        {
            _metrics.EventsDropped++;
            return;
        }

        var receivedAtNs = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000;

        switch (envelope.MessageType)
        {
            case MessageTypes.OrderAccepted:
                DispatchOrderAccepted(envelope.Payload, envelope.CorrelationId, receivedAtNs);
                break;

            case MessageTypes.OrderRejected:
                DispatchOrderRejected(envelope.Payload, envelope.InstrumentId, envelope.CorrelationId, receivedAtNs);
                break;

            case MessageTypes.OrderCancelled:
                DispatchOrderCancelled(envelope.Payload, envelope.CorrelationId, receivedAtNs);
                break;

            case MessageTypes.OrderExecuted:
                DispatchOrderExecuted(envelope.Payload, receivedAtNs);
                break;

            // LOCKED: MarketOrderRemainderCancelled is a lifecycle event on the
            // order (matched what it could; remainder cancelled for insufficient
            // liquidity). It is NOT a pre-match reject. Routes to the orders
            // table with action="cancel_remainder" to disambiguate from a
            // team-initiated cancel.
            case MessageTypes.MarketOrderRemainderCancelled:
                DispatchMarketOrderRemainderCancelled(envelope.Payload, envelope.CorrelationId, receivedAtNs);
                break;

            case MessageTypes.BookDelta:
                DispatchBookDelta(envelope.Payload, receivedAtNs);
                break;

            case MessageTypes.PublicTrade:
                DispatchPublicTrade(envelope.Payload, receivedAtNs);
                break;

            case MessageTypes.ImbalanceSettlement:
                DispatchImbalanceSettlement(envelope.Payload, receivedAtNs);
                break;

            default:
                DispatchEvent(envelope.MessageType, envelope.Payload, receivedAtNs);
                break;
        }
    }

    private void DispatchOrderAccepted(JsonElement payload, string? correlationId, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.OrderAcceptedEvent);
        if (e is null) return;

        TryWrite(new OrderWrite(
            TsNs: e.TimestampNs,
            ClientId: e.ClientId,
            InstrumentId: FormatInstrument(e.InstrumentId),
            OrderId: e.OrderId,
            Action: "submit",
            Side: e.Side,
            PriceTicks: e.PriceTicks,
            Quantity: e.Quantity,
            OrderType: e.OrderType,
            CorrelationId: correlationId,
            ReceivedAtNs: receivedAtNs));
    }

    private void DispatchOrderRejected(JsonElement payload, string? instrumentId, string? correlationId, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.OrderRejectedEvent);
        if (e is null) return;

        TryWrite(new RejectWrite(
            TsNs: e.TimestampNs,
            ClientId: e.ClientId,
            InstrumentId: instrumentId,
            RejectionCode: e.Reason,
            ReasonDetail: null,
            CorrelationId: correlationId,
            ReceivedAtNs: receivedAtNs));
    }

    private void DispatchOrderCancelled(JsonElement payload, string? correlationId, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.OrderCancelledEvent);
        if (e is null) return;

        TryWrite(new OrderWrite(
            TsNs: e.TimestampNs,
            ClientId: e.ClientId,
            InstrumentId: FormatInstrument(e.InstrumentId),
            OrderId: e.OrderId,
            Action: "cancel",
            Side: null,
            PriceTicks: null,
            Quantity: e.RemainingQuantity,
            OrderType: null,
            CorrelationId: correlationId,
            ReceivedAtNs: receivedAtNs));
    }

    private void DispatchOrderExecuted(JsonElement payload, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.OrderExecutedEvent);
        if (e is null) return;

        // BIFROST fills table carries both sides of a trade. The envelope's
        // OrderExecutedEvent is addressed to a single client (either maker or
        // taker depending on IsAggressor). We stamp the addressed side and
        // leave the counterparty blank; the gateway / later correlation pass
        // pairs the two halves by trade_id.
        var makerClientId = e.IsAggressor ? string.Empty : e.ClientId;
        var takerClientId = e.IsAggressor ? e.ClientId : string.Empty;
        var makerOrderId = e.IsAggressor ? 0L : e.OrderId;
        var takerOrderId = e.IsAggressor ? e.OrderId : 0L;

        TryWrite(new FillWrite(
            TsNs: e.TimestampNs,
            InstrumentId: FormatInstrument(e.InstrumentId),
            TradeId: e.TradeId,
            PriceTicks: e.PriceTicks,
            Quantity: e.FilledQuantity,
            AggressorSide: e.Side,
            MakerClientId: makerClientId,
            TakerClientId: takerClientId,
            MakerOrderId: makerOrderId,
            TakerOrderId: takerOrderId,
            ReceivedAtNs: receivedAtNs));
    }

    private void DispatchMarketOrderRemainderCancelled(JsonElement payload, string? correlationId, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.MarketOrderRemainderCancelledEvent);
        if (e is null) return;

        TryWrite(new OrderWrite(
            TsNs: e.TimestampNs,
            ClientId: e.ClientId,
            InstrumentId: FormatInstrument(e.InstrumentId),
            OrderId: e.OrderId,
            Action: "cancel_remainder",
            Side: null,
            PriceTicks: null,
            Quantity: e.CancelledQuantity,
            OrderType: "Market",
            CorrelationId: correlationId,
            ReceivedAtNs: receivedAtNs));
    }

    private void DispatchBookDelta(JsonElement payload, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.BookDeltaEvent);
        if (e is null) return;

        var instrumentId = FormatInstrument(e.InstrumentId);

        // Each changed level becomes one book_updates row. Level index is the
        // array position in the event payload — consumers that want best-first
        // ordering sort by price (desc for bids, asc for asks) at read time.
        for (var i = 0; i < e.ChangedBids.Length; i++)
        {
            var lvl = e.ChangedBids[i];
            TryWrite(new BookUpdateWrite(
                TsNs: e.TimestampNs,
                InstrumentId: instrumentId,
                Side: "Buy",
                Level: i,
                PriceTicks: lvl.PriceTicks,
                Quantity: lvl.Quantity,
                Count: lvl.OrderCount,
                Sequence: e.Sequence,
                ReceivedAtNs: receivedAtNs));
        }

        for (var i = 0; i < e.ChangedAsks.Length; i++)
        {
            var lvl = e.ChangedAsks[i];
            TryWrite(new BookUpdateWrite(
                TsNs: e.TimestampNs,
                InstrumentId: instrumentId,
                Side: "Sell",
                Level: i,
                PriceTicks: lvl.PriceTicks,
                Quantity: lvl.Quantity,
                Count: lvl.OrderCount,
                Sequence: e.Sequence,
                ReceivedAtNs: receivedAtNs));
        }
    }

    private void DispatchPublicTrade(JsonElement payload, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.PublicTradeEvent);
        if (e is null) return;

        TryWrite(new TradeWrite(
            TsNs: e.TimestampNs,
            InstrumentId: FormatInstrument(e.InstrumentId),
            TradeId: e.TradeId,
            PriceTicks: e.PriceTicks,
            Quantity: e.Quantity,
            AggressorSide: e.AggressorSide,
            Sequence: e.Sequence,
            ReceivedAtNs: receivedAtNs));
    }

    private void DispatchImbalanceSettlement(JsonElement payload, long receivedAtNs)
    {
        var e = payload.Deserialize(RecorderJsonContext.Default.ImbalanceSettlementEvent);
        if (e is null) return;

        TryWrite(new ImbalanceSettlementWrite(
            TsNs: e.TimestampNs,
            RoundNumber: e.RoundNumber,
            ClientId: e.ClientId,
            InstrumentId: FormatInstrument(e.InstrumentId),
            QuarterIndex: e.QuarterIndex,
            PositionTicks: e.PositionTicks,
            PImbTicks: e.PImbTicks,
            ImbalancePnlTicks: e.ImbalancePnlTicks,
            ReceivedAtNs: receivedAtNs));
    }

    private void DispatchEvent(string kind, JsonElement payload, long receivedAtNs)
    {
        // Unknown or unmapped event — store the raw payload JSON and the
        // envelope kind for later analysis. Severity defaults to "info";
        // future plans may promote known kinds to "urgent".
        TryWrite(new EventWrite(
            TsNs: receivedAtNs,
            Kind: kind,
            Severity: "info",
            PayloadJson: payload.GetRawText(),
            ReceivedAtNs: receivedAtNs));
    }

    private void TryWrite(WriteCommand cmd)
    {
        if (!_channelWriter.TryWrite(cmd))
        {
            _metrics.EventsDropped++;
            OnDropped();
        }
        else
        {
            OnWriteSuccess();
        }
    }

    private static string FormatInstrument(InstrumentIdDto id) =>
        $"{id.DeliveryArea}-{id.DeliveryPeriodStart:yyyyMMddTHHmm}-{id.DeliveryPeriodEnd:yyyyMMddTHHmm}";

    private void OnDropped()
    {
        if (!_dropStopwatch.IsRunning)
            _dropStopwatch.Start();

        if (_dropStopwatch.Elapsed >= TimeSpan.FromSeconds(5) && !_isDegraded)
        {
            _isDegraded = true;
            _metrics.IsDegraded = true;
            _logger.LogWarning(
                "Entering degraded mode: dropping events (channel full for {Elapsed})",
                _dropStopwatch.Elapsed);
            _dropStopwatch.Reset();
        }
    }

    private void OnWriteSuccess()
    {
        if (_dropStopwatch.IsRunning)
        {
            _dropStopwatch.Reset();
        }
    }

    private void CheckDegradationExit()
    {
        if (!_isDegraded) return;

        var ratio = (double)_channel.Reader.Count / _channelCapacity;
        if (ratio < 0.5)
        {
            _isDegraded = false;
            _metrics.IsDegraded = false;
            _logger.LogWarning("Exiting degraded mode: channel depth below 50%");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        var exitReason = _exitDetector.Detect(true);
        _manifest.ExitReason = exitReason;
        _manifest.EndTime = _clock.GetUtcNow();

        _channelWriter.Complete();
        await Task.Delay(500, CancellationToken.None);

        var counts = _db.GetEventCounts();
        _manifest.EventCounts = new ManifestEventCounts
        {
            BookUpdates = counts.BookUpdates,
            Trades = counts.Trades,
            Orders = counts.Orders,
            Fills = counts.Fills,
            Rejects = counts.Rejects,
            Events = counts.Events,
        };

        _sessionManager.WriteManifest(_sessionDir, _manifest);

        _sessionIndex.AddEntry(new SessionIndexEntry
        {
            RunId = _manifest.RunId,
            Name = _manifest.Name,
            StartTime = _manifest.StartTime,
            EndTime = _manifest.EndTime,
            TeamCount = _manifest.ParticipatingTeams.Count,
            InstrumentCount = _manifest.InstrumentCount,
        });

        if (_consumeChannel is not null)
            await _consumeChannel.CloseAsync(cancellationToken: cancellationToken);

        if (_connection is not null)
            await _connection.CloseAsync(cancellationToken);
    }
}
