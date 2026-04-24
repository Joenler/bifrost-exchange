using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
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
/// End-to-end coverage of the ImbalanceSettlement persistence path:
/// synthetic RabbitMQ envelope → <see cref="RabbitMqRecorderConsumer.DispatchMessage"/>
/// → <see cref="Channel{WriteCommand}"/> → <see cref="WriteLoop"/>
/// → <see cref="SessionDatabase.InsertImbalanceSettlements"/> → one row
/// in the <c>imbalance_settlements</c> table with the exact integer
/// product PositionTicks × PImbTicks preserved end-to-end.
/// </summary>
public sealed class RecorderImbalanceSettlementTests : IDisposable
{
    private sealed class FakeClock(FakeTimeProvider provider) : IClock
    {
        public DateTimeOffset GetUtcNow() => provider.GetUtcNow();
    }

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

    public RecorderImbalanceSettlementTests()
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
    public async Task EveryImbalanceSettlementEvent_IsPersisted()
    {
        // Integer-equality invariant: 16 MWh × ticks_per_MWh × €52 × 100 ticks/€
        // land as exact integer product — no floating-point drift.
        const long positionTicks = 16_000L;
        const long pImbTicks = 5_200_000L;
        const long expectedPnlTicks = positionTicks * pImbTicks;

        var body = BuildEnvelopeBytes(
            MessageTypes.ImbalanceSettlement,
            new ImbalanceSettlementEvent(
                RoundNumber: 42,
                ClientId: "alpha",
                InstrumentId: MakeInstrument(),
                QuarterIndex: 2,
                PositionTicks: positionTicks,
                PImbTicks: pImbTicks,
                ImbalancePnlTicks: expectedPnlTicks,
                TimestampNs: 1_745_400_000_000_000_100L));

        _consumer.DispatchMessage(body);

        await DrainAndFlushAsync();

        var rows = _db.Query<(long ts_ns, int round_number, string client_id, string instrument_id,
                              int quarter_index, long position_ticks, long p_imb_ticks, long imbalance_pnl_ticks)>(
            "SELECT ts_ns, round_number, client_id, instrument_id, quarter_index, " +
            "position_ticks, p_imb_ticks, imbalance_pnl_ticks FROM imbalance_settlements").ToList();

        Assert.Single(rows);
        Assert.Equal(1_745_400_000_000_000_100L, rows[0].ts_ns);
        Assert.Equal(42, rows[0].round_number);
        Assert.Equal("alpha", rows[0].client_id);
        Assert.Equal(2, rows[0].quarter_index);
        Assert.Equal(positionTicks, rows[0].position_ticks);
        Assert.Equal(pImbTicks, rows[0].p_imb_ticks);
        Assert.Equal(16_000L * 5_200_000L, rows[0].imbalance_pnl_ticks);
    }

    [Fact]
    public void UniqueConstraint_PreventsDuplicateRoundClientQuarter()
    {
        // Direct storage-path exercise: the UNIQUE(round_number, client_id,
        // quarter_index) constraint on the imbalance_settlements table must
        // reject a second insert for the same (round, client, quarter)
        // triplet. Catches silent-overwrite regressions at the DB layer
        // independently of the envelope-dispatch path.
        var write = new ImbalanceSettlementWrite(
            TsNs: 1L,
            RoundNumber: 42,
            ClientId: "alpha",
            InstrumentId: "DE-99990101T0000-99990101T0100",
            QuarterIndex: 2,
            PositionTicks: 1L,
            PImbTicks: 1L,
            ImbalancePnlTicks: 1L,
            ReceivedAtNs: 1L);

        _db.InsertImbalanceSettlements(new List<ImbalanceSettlementWrite> { write });

        var ex = Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() =>
            _db.InsertImbalanceSettlements(new List<ImbalanceSettlementWrite> { write }));
        Assert.Contains("UNIQUE", ex.Message);
    }

    // ----- helpers -----

    private static InstrumentIdDto MakeInstrument() =>
        new(
            DeliveryArea: "DE",
            DeliveryPeriodStart: new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DeliveryPeriodEnd: new DateTimeOffset(9999, 1, 1, 0, 15, 0, TimeSpan.Zero));

    private static byte[] BuildEnvelopeBytes<T>(string messageType, T payload)
    {
        var payloadElement = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var envelope = new Envelope<JsonElement>(
            MessageType: messageType,
            TimestampUtc: DateTimeOffset.UnixEpoch,
            CorrelationId: "corr-imb-123",
            ClientId: null,
            InstrumentId: null,
            Sequence: null,
            Payload: payloadElement);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    private async Task DrainAndFlushAsync()
    {
        // WriteLoop flushes on a 250ms timer OR whenever a batch fills. Allow
        // one flush interval plus slack — mirrors RecorderPersistenceTests.
        await Task.Delay(500);
    }
}
