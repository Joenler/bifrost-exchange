using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Tests.Fixtures;
using Xunit;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Streaming;

/// <summary>
/// GW-06 + D-06a: gateway is the sole authority on each team's position.
///
/// Acceptance covers two halves of the contract:
///   1. RegisterAck → 5 PositionSnapshots in canonical instrument order
///      (H1, Q1, Q2, Q3, Q4) — the burst.
///   2. Each Fill (OrderExecutedEvent) is followed IMMEDIATELY by a
///      PositionSnapshot reflecting the post-fill (NetPositionTicks, VWAP).
///      Adjacency is the wire-level guarantee D-06a names.
/// </summary>
[Collection("Gateway")]
public sealed class PositionAuthorityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GatewayTestHost _host;

    public PositionAuthorityTests(GatewayTestHost host) => _host = host;

    [Fact]
    public async Task RegisterAck_FollowedBy5PositionSnapshots_InCanonicalInstrumentOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "position-authority-burst-team";
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
        }, ct);

        Assert.True(await call.ResponseStream.MoveNext(ct));
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.RegisterAck,
            call.ResponseStream.Current.EventCase);

        // 5 PositionSnapshots in canonical InstrumentOrdering order: H1, Q1, Q2, Q3, Q4.
        string[] expected = { "H1", "Q1", "Q2", "Q3", "Q4" };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(await call.ResponseStream.MoveNext(ct));
            var snap = call.ResponseStream.Current;
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PositionSnapshot, snap.EventCase);
            Assert.Equal(expected[i], snap.PositionSnapshot.Instrument.InstrumentId);
            // Fresh team: net position is zero on every instrument.
            Assert.Equal(0L, snap.PositionSnapshot.NetPositionTicks);
        }

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task EachFill_FollowedByPositionSnapshot_AdjacentInSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "position-authority-adjacency-team";
        var consumer = _host.GetPrivateEventConsumer();

        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
        }, ct);
        // Drain RegisterAck + 5 PositionSnapshot burst.
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var clientId = call.ResponseStream.Current.RegisterAck.ClientId;
        for (var i = 0; i < 5; i++) Assert.True(await call.ResponseStream.MoveNext(ct));

        // Inject 3 OrderExecuted events on H1 — each should land as Fill+Snapshot
        // adjacent on the response stream (D-06a).
        for (var i = 0; i < 3; i++)
        {
            var envelope = NewOrderExecutedEnvelope(clientId, "H1",
                tradeId: 2000 + i, orderId: 3000 + i,
                priceTicks: 100 + 10 * i, filledQty: 1m);
            await consumer.DispatchEnvelopeAsync(envelope, ct);
        }

        // Read 6 frames (3 × Fill + Snapshot pair) and assert adjacency.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await call.ResponseStream.MoveNext(ct));
            var fill = call.ResponseStream.Current;
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.Fill, fill.EventCase);

            Assert.True(await call.ResponseStream.MoveNext(ct));
            var snap = call.ResponseStream.Current;
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PositionSnapshot, snap.EventCase);
            Assert.Equal("H1", snap.PositionSnapshot.Instrument.InstrumentId);
        }

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task PositionSnapshotMath_NetAndVwap_OverMultipleFills()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "position-authority-math-team";
        var consumer = _host.GetPrivateEventConsumer();

        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
        }, ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var clientId = call.ResponseStream.Current.RegisterAck.ClientId;
        for (var i = 0; i < 5; i++) Assert.True(await call.ResponseStream.MoveNext(ct));

        // 3 buys on H1, all qty=1 MWh: prices 100, 200, 300 → final VWAP = 200,
        // net = +3 MWh in ticks (3 * QuantityScale.TicksPerUnit = 30000).
        long[] prices = { 100, 200, 300 };
        for (var i = 0; i < prices.Length; i++)
        {
            await consumer.DispatchEnvelopeAsync(
                NewOrderExecutedEnvelope(clientId, "H1",
                    tradeId: 4000 + i, orderId: 5000 + i,
                    priceTicks: prices[i], filledQty: 1m), ct);
        }

        // Read 3 Fill+Snapshot pairs; capture the LAST snapshot's net + VWAP.
        long lastNet = 0, lastVwap = 0;
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await call.ResponseStream.MoveNext(ct));   // Fill
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.Fill, call.ResponseStream.Current.EventCase);

            Assert.True(await call.ResponseStream.MoveNext(ct));   // PositionSnapshot
            var snap = call.ResponseStream.Current;
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PositionSnapshot, snap.EventCase);
            lastNet = snap.PositionSnapshot.NetPositionTicks;
            lastVwap = snap.PositionSnapshot.AveragePriceTicks;
        }

        // 3 × 1 MWh = 3 MWh; QuantityScale.TicksPerUnit = 10000 → 30000 ticks.
        Assert.Equal(30_000L, lastNet);
        // VWAP after 3 equal-qty fills at 100/200/300 = (100+200+300)/3 = 200.
        Assert.Equal(200L, lastVwap);

        await call.RequestStream.CompleteAsync();
    }

    private static Envelope<JsonElement> NewOrderExecutedEnvelope(
        string clientId, string instrumentId, long tradeId, long orderId,
        long priceTicks, decimal filledQty)
    {
        var instrument = InstrumentForId(instrumentId);
        var dto = new OrderExecutedEvent(
            TradeId: tradeId,
            OrderId: orderId,
            ClientId: clientId,
            InstrumentId: instrument,
            PriceTicks: priceTicks,
            FilledQuantity: filledQty,
            RemainingQuantity: 0m,
            Side: "Buy",
            IsAggressor: true,
            Fee: 0m,
            TimestampNs: 0L);
        var payload = JsonSerializer.SerializeToElement(dto, JsonOptions);
        return new Envelope<JsonElement>(
            MessageType: MessageTypes.OrderExecuted,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            ClientId: clientId,
            InstrumentId: instrumentId,
            Sequence: tradeId,
            Payload: payload);
    }

    private static InstrumentIdDto InstrumentForId(string id)
    {
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return id switch
        {
            "H1" => new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1)),
            "Q1" => new InstrumentIdDto("DE", hourStart, hourStart.AddMinutes(15)),
            "Q2" => new InstrumentIdDto("DE", hourStart.AddMinutes(15), hourStart.AddMinutes(30)),
            "Q3" => new InstrumentIdDto("DE", hourStart.AddMinutes(30), hourStart.AddMinutes(45)),
            "Q4" => new InstrumentIdDto("DE", hourStart.AddMinutes(45), hourStart.AddHours(1)),
            _ => new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1)),
        };
    }
}
