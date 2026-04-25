using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Internal.McLog;
using Bifrost.Recorder.Infrastructure;
using Bifrost.Recorder.Session;
using Bifrost.Recorder.Storage;
using Bifrost.Time;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Recorder.Tests;

/// <summary>
/// REC-01 end-to-end coverage: synthetic RabbitMQ envelopes flow through
/// <see cref="RabbitMqRecorderConsumer.DispatchMessage"/> (InternalsVisibleTo)
/// into the <see cref="Channel{WriteCommand}"/>, are drained by
/// <see cref="WriteLoop"/>, and land as rows in the expected BIFROST table.
/// Every subtype-dispatch path is exercised — including the locked
/// MarketOrderRemainderCancelled → <c>OrderWrite(action="cancel_remainder")</c>
/// mapping.
/// </summary>
public sealed class RecorderPersistenceTests : IDisposable
{
    private sealed class FakeClock(FakeTimeProvider provider) : IClock
    {
        public DateTimeOffset GetUtcNow() => provider.GetUtcNow();
    }

    // JSON options matching the source-generated RecorderJsonContext CamelCase
    // convention so the deserializer inside DispatchMessage can unwrap the
    // envelope.Payload field names that the test writes as CamelCase JSON.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly FakeTimeProvider _provider = new(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeClock _clock;
    private readonly SessionDatabase _db;
    private readonly Channel<WriteCommand> _channel;
    private readonly RecorderMetrics _metrics;
    private readonly WriteLoop _writeLoop;
    private readonly RabbitMqRecorderConsumer _consumer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writeTask;

    public RecorderPersistenceTests()
    {
        _clock = new FakeClock(_provider);

        _db = new SessionDatabase("Data Source=:memory:");
        _db.InitializePragmas();
        var migrator = new SchemaMigrator(_db, _clock, NullLogger<SchemaMigrator>.Instance);
        migrator.ApplyPending();

        SqlMapper.AddTypeHandler(new DecimalTypeHandler());
        SqlMapper.AddTypeHandler(new BoolTypeHandler());

        _channel = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(1_000)
        {
            SingleReader = true,
        });
        _metrics = new RecorderMetrics();

        _writeLoop = new WriteLoop(_channel, _db, _metrics, NullLogger<WriteLoop>.Instance, channelCapacity: 1_000);
        _writeTask = _writeLoop.StartAsync(_cts.Token);

        var sessionsRoot = Path.Combine(Path.GetTempPath(), "bifrost-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionsRoot);
        var sessionManager = new SessionManager(_clock, NullLogger<SessionManager>.Instance);
        var sessionDir = sessionManager.CreateSessionDirectory(sessionsRoot, "test-run");
        var sessionIndex = new SessionIndex(sessionsRoot);
        var exitDetector = new ExitReasonDetector(_clock, TimeSpan.FromSeconds(30));
        var manifest = new Manifest { RunId = "test-run", StartTime = _clock.GetUtcNow() };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        _consumer = new RabbitMqRecorderConsumer(
            configuration,
            _channel,
            sessionManager,
            sessionIndex,
            exitDetector,
            manifest,
            sessionDir,
            _db,
            _metrics,
            _clock,
            NullLogger<RabbitMqRecorderConsumer>.Instance,
            channelCapacity: 1_000);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _writeLoop.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Test teardown — swallow.
        }

        _cts.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task BookDelta_LandsInBookUpdatesTable_WithChangedLevelsFannedOut()
    {
        var body = BuildEnvelopeBytes(
            MessageTypes.BookDelta,
            new BookDeltaEvent(
                InstrumentId: MakeInstrument(),
                Sequence: 42L,
                ChangedBids:
                [
                    new BookLevelDto(PriceTicks: 5000L, Quantity: 10m, OrderCount: 2),
                ],
                ChangedAsks:
                [
                    new BookLevelDto(PriceTicks: 5100L, Quantity: 8m, OrderCount: 1),
                ],
                TimestampNs: 111_000_000_000L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var rows = _db.Query<(long ts_ns, string instrument_id, string side, int level, long price_ticks, long sequence)>(
            "SELECT ts_ns, instrument_id, side, level, price_ticks, sequence FROM book_updates ORDER BY side, level").ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Buy", rows[0].side);
        Assert.Equal(5000L, rows[0].price_ticks);
        Assert.Equal(42L, rows[0].sequence);
        Assert.Equal("Sell", rows[1].side);
        Assert.Equal(5100L, rows[1].price_ticks);
    }

    [Fact]
    public async Task PublicTrade_LandsInTradesTable()
    {
        var body = BuildEnvelopeBytes(
            MessageTypes.PublicTrade,
            new PublicTradeEvent(
                TradeId: 7L,
                InstrumentId: MakeInstrument(),
                PriceTicks: 5050L,
                Quantity: 3m,
                AggressorSide: "Buy",
                TickSize: 1L,
                Sequence: 99L,
                TimestampNs: 222_000_000_000L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(long trade_id, long price_ticks, string aggressor_side, long sequence)>(
            "SELECT trade_id, price_ticks, aggressor_side, sequence FROM trades").Single();

        Assert.Equal(7L, row.trade_id);
        Assert.Equal(5050L, row.price_ticks);
        Assert.Equal("Buy", row.aggressor_side);
        Assert.Equal(99L, row.sequence);
    }

    [Fact]
    public async Task OrderAccepted_LandsInOrdersTable_WithActionSubmit()
    {
        var body = BuildEnvelopeBytes(
            MessageTypes.OrderAccepted,
            new OrderAcceptedEvent(
                OrderId: 100L,
                ClientId: "team-red",
                InstrumentId: MakeInstrument(),
                Side: "Buy",
                OrderType: "Limit",
                PriceTicks: 5000L,
                Quantity: 12m,
                DisplaySliceSize: null,
                TimestampNs: 333_000_000_000L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(long order_id, string client_id, string action, string side, string order_type)>(
            "SELECT order_id, client_id, action, side, order_type FROM orders").Single();

        Assert.Equal(100L, row.order_id);
        Assert.Equal("team-red", row.client_id);
        Assert.Equal("submit", row.action);
        Assert.Equal("Buy", row.side);
        Assert.Equal("Limit", row.order_type);
    }

    [Fact]
    public async Task OrderRejected_LandsInRejectsTable()
    {
        var body = BuildEnvelopeBytes(
            MessageTypes.OrderRejected,
            new OrderRejectedEvent(
                OrderId: 101L,
                ClientId: "team-blue",
                Reason: "InvalidSide",
                TimestampNs: 444_000_000_000L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(string client_id, string rejection_code)>(
            "SELECT client_id, rejection_code FROM rejects").Single();

        Assert.Equal("team-blue", row.client_id);
        Assert.Equal("InvalidSide", row.rejection_code);
    }

    [Fact]
    public async Task OrderCancelled_LandsInOrdersTable_WithActionCancel()
    {
        var body = BuildEnvelopeBytes(
            MessageTypes.OrderCancelled,
            new OrderCancelledEvent(
                OrderId: 102L,
                ClientId: "team-green",
                InstrumentId: MakeInstrument(),
                RemainingQuantity: 4m,
                TimestampNs: 555_000_000_000L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(long order_id, string client_id, string action)>(
            "SELECT order_id, client_id, action FROM orders").Single();

        Assert.Equal(102L, row.order_id);
        Assert.Equal("team-green", row.client_id);
        Assert.Equal("cancel", row.action);
    }

    [Fact]
    public async Task OrderExecuted_LandsInFillsTable()
    {
        var body = BuildEnvelopeBytes(
            MessageTypes.OrderExecuted,
            new OrderExecutedEvent(
                TradeId: 999L,
                OrderId: 103L,
                ClientId: "team-yellow",
                InstrumentId: MakeInstrument(),
                PriceTicks: 5075L,
                FilledQuantity: 6m,
                RemainingQuantity: 0m,
                Side: "Buy",
                IsAggressor: true,
                Fee: 0.1m,
                TimestampNs: 666_000_000_000L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(long trade_id, long price_ticks, string aggressor_side, string taker_client_id, long taker_order_id)>(
            "SELECT trade_id, price_ticks, aggressor_side, taker_client_id, taker_order_id FROM fills").Single();

        Assert.Equal(999L, row.trade_id);
        Assert.Equal(5075L, row.price_ticks);
        Assert.Equal("Buy", row.aggressor_side);
        // Client is the aggressor (taker) side per dispatch logic.
        Assert.Equal("team-yellow", row.taker_client_id);
        Assert.Equal(103L, row.taker_order_id);
    }

    [Fact]
    public async Task MarketOrderRemainderCancelled_LandsInOrdersTable_WithActionCancelRemainder()
    {
        // LOCKED dispatch: MarketOrderRemainderCancelled is a lifecycle event
        // on an order (matched what it could; remainder cancelled for
        // insufficient liquidity). It is NOT a pre-match refusal, so it must
        // land in the orders table with the new action verb "cancel_remainder",
        // NOT in the rejects table.
        var body = BuildEnvelopeBytes(
            MessageTypes.MarketOrderRemainderCancelled,
            new MarketOrderRemainderCancelledEvent(
                OrderId: 104L,
                ClientId: "team-purple",
                InstrumentId: MakeInstrument(),
                CancelledQuantity: 2m,
                TimestampNs: 777_000_000_000L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var orderRow = _db.Query<(long order_id, string client_id, string action, string order_type)>(
            "SELECT order_id, client_id, action, order_type FROM orders").Single();

        Assert.Equal(104L, orderRow.order_id);
        Assert.Equal("team-purple", orderRow.client_id);
        Assert.Equal("cancel_remainder", orderRow.action);
        Assert.Equal("Market", orderRow.order_type);

        // Confirm NOT in rejects.
        var rejectRows = _db.Query<int>("SELECT COUNT(*) FROM rejects").Single();
        Assert.Equal(0, rejectRows);
    }

    [Fact]
    public async Task UnknownMessageType_LandsInEventsTable()
    {
        // News / alerts / round-state / any unmapped message type fans into
        // the public events table verbatim.
        var payloadObj = new { headline = "Generator trip at DE-NorthPool-3", severity = "urgent" };
        var body = BuildEnvelopeBytes("PhysicalShock", payloadObj);

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(string kind, string severity, string payload_json)>(
            "SELECT kind, severity, payload_json FROM events").Single();

        Assert.Equal("PhysicalShock", row.kind);
        Assert.Equal("info", row.severity);
        Assert.Contains("Generator trip", row.payload_json);
    }

    [Fact]
    public async Task McCommandLog_LandsInMcCommandsTable_WithResultJsonAndOperatorHostname()
    {
        // Phase 06 D-23: bifrost.mc.v1/mc.command.# audit envelopes land in the
        // mc_commands table (Phase 02 shipped the table empty; Phase 06 fills
        // it). Both accepted and rejected commands are audit-logged — the
        // rejected branch carries Success=false in the synthesised result_json.
        var body = BuildEnvelopeBytes(
            MessageTypes.McCommandLog,
            new McCommandLogPayload(
                TimestampNs: 1_111_000_000_000L,
                Command: "AuctionOpen",
                ArgsJson: "{\"operator_host\":\"mc\",\"confirm\":true}",
                Success: true,
                Message: "transitioned to AuctionOpen",
                NewStateJson: "{\"state\":\"STATE_AUCTION_OPEN\"}",
                OperatorHostname: "mc"));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(long ts_ns, string command, string args_json, string result_json, string operator_hostname)>(
            "SELECT ts_ns, command, args_json, result_json, operator_hostname FROM mc_commands").Single();

        Assert.Equal(1_111_000_000_000L, row.ts_ns);
        Assert.Equal("AuctionOpen", row.command);
        Assert.Contains("\"confirm\":true", row.args_json);
        // result_json is a synthesised composite of (success, message, new_state)
        // — the recorder builds it from the three audit fields per CONTEXT D-23.
        Assert.Contains("\"success\":true", row.result_json);
        Assert.Contains("transitioned to AuctionOpen", row.result_json);
        Assert.Equal("mc", row.operator_hostname);
    }

    [Fact]
    public async Task McCommandLog_RejectedCommand_PreservesSuccessFalseInResultJson()
    {
        // Audit-log invariant (SPEC Req 10): rejected commands are not dropped.
        // Their envelope arrives with Success=false and the rejection detail
        // in Message; the recorder writes a row carrying success:false in
        // result_json so post-event replay tools see the rejection.
        var body = BuildEnvelopeBytes(
            MessageTypes.McCommandLog,
            new McCommandLogPayload(
                TimestampNs: 2_222_000_000_000L,
                Command: "Gate",
                ArgsJson: "{\"operator_host\":\"mc\",\"confirm\":false}",
                Success: false,
                Message: "confirm required for Gate",
                NewStateJson: string.Empty,
                OperatorHostname: "mc"));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var row = _db.Query<(string command, string result_json)>(
            "SELECT command, result_json FROM mc_commands").Single();

        Assert.Equal("Gate", row.command);
        Assert.Contains("\"success\":false", row.result_json);
        Assert.Contains("confirm required for Gate", row.result_json);
    }

    // ----- helpers -----

    private static InstrumentIdDto MakeInstrument() =>
        new(
            DeliveryArea: "DE",
            DeliveryPeriodStart: new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DeliveryPeriodEnd: new DateTimeOffset(9999, 1, 1, 1, 0, 0, TimeSpan.Zero));

    private static byte[] BuildEnvelopeBytes<T>(string messageType, T payload)
    {
        // The consumer deserializes the envelope via the source-generated
        // context which targets Envelope<JsonElement>. We serialize the
        // envelope + payload through plain JsonSerializer so the Payload
        // field is whatever JSON shape DispatchMessage's second Deserialize
        // expects via RecorderJsonContext.Default.<EventType>.
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var envelope = new Envelope<JsonElement>(
            MessageType: messageType,
            TimestampUtc: DateTimeOffset.UnixEpoch,
            CorrelationId: "corr-123",
            ClientId: null,
            InstrumentId: null,
            Sequence: null,
            Payload: payloadElement);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    private async Task DrainAndFlushAsync()
    {
        // WriteLoop flushes on a 250ms timer OR whenever a batch fills. Allow
        // one flush interval plus slack.
        await Task.Delay(500);
    }
}
