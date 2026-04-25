using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Gateway.Tests.Fixtures;
using Xunit;
using AppRoundState = Bifrost.Exchange.Application.RoundState;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Streaming;

/// <summary>
/// SPEC req 4 + 5 (GW-03 + GW-04 acceptance): the 6-guard ADR-0004 chain.
/// Order is fixed: structural → state-gate → msg-rate → OTR → max-notional →
/// max-open-orders → max-position → self-trade. First failure short-circuits
/// later guards; cancels bypass tiers 3-5.
///
/// Each [Fact] below drives a single OrderSubmit through the in-process gRPC
/// surface and asserts the OrderReject reason matches the expected guard.
/// </summary>
[Collection("Gateway")]
public sealed class GuardChainAcceptanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GatewayTestHost _host;

    public GuardChainAcceptanceTests(GatewayTestHost host) => _host = host;

    [Fact]
    public async Task OrderSubmit_DuringGate_RejectedWithExchangeClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-state-gate-team";
        _host.Round.SetState(AppRoundState.RoundState.Gate);
        try
        {
            using var call = await OpenStreamAndDrainAckAsync(team, ct);
            await call.RequestStream.WriteAsync(SubmitFor("H1", MarketProto.Side.Buy, priceTicks: 100, qtyTicks: 100), ct);
            Assert.True(await call.ResponseStream.MoveNext(ct));
            var resp = call.ResponseStream.Current;
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
            Assert.Equal(StrategyProto.RejectReason.ExchangeClosed, resp.OrderReject.Reason);
            await call.RequestStream.CompleteAsync();
        }
        finally
        {
            _host.Round.SetState(AppRoundState.RoundState.RoundOpen);
        }
    }

    [Fact]
    public async Task OrderCancel_DuringGate_NotRejectedByStateGate()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-cancel-bypass-team";
        _host.Round.SetState(AppRoundState.RoundState.Gate);
        try
        {
            using var call = await OpenStreamAndDrainAckAsync(team, ct);
            // Cancel bypasses state-gate per ADR-0004 + Phase 02 D-09. The gateway
            // does NOT have a resting order to actually cancel, but the guard chain's
            // state-gate guard returns Ok for OrderCancel regardless.
            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                OrderCancel = new StrategyProto.OrderCancel
                {
                    ClientId = string.Empty,
                    OrderId = 12345,
                    Instrument = NewInstrument("H1"),
                },
            }, ct);
            // The guard chain accepts; the publisher records it. No ExchangeClosed reject
            // is sent. Close the request stream so the response stream completes.
            await call.RequestStream.CompleteAsync();
            // Drain remaining frames; assert NO OrderReject(EXCHANGE_CLOSED) appears.
            while (await call.ResponseStream.MoveNext(ct))
            {
                var ev = call.ResponseStream.Current;
                if (ev.EventCase == StrategyProto.MarketEvent.EventOneofCase.OrderReject)
                {
                    Assert.NotEqual(StrategyProto.RejectReason.ExchangeClosed, ev.OrderReject.Reason);
                }
            }
        }
        finally
        {
            _host.Round.SetState(AppRoundState.RoundState.RoundOpen);
        }
    }

    [Fact]
    public async Task Submit_StructuralAndPosition_StructuralWinsOrder()
    {
        // Tier 1 (structural) runs before Tier 4 (max-position). A frame that
        // would fail BOTH must be rejected as STRUCTURAL.
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-structural-first-team";
        using var call = await OpenStreamAndDrainAckAsync(team, ct);

        // Submit with quantity_ticks=0 (structural failure) on an oversized notional
        // would-be (if we had a positive qty); structural fires first.
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            OrderSubmit = new StrategyProto.OrderSubmit
            {
                ClientId = string.Empty,
                Instrument = NewInstrument("H1"),
                Side = MarketProto.Side.Buy,
                OrderType = MarketProto.OrderType.Limit,
                PriceTicks = 100,
                QuantityTicks = 0,   // structural reject: qty must be > 0
            },
        }, ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var resp = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
        Assert.Equal(StrategyProto.RejectReason.Structural, resp.OrderReject.Reason);
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Submit_NotionalOver50Mwh_MaxNotionalReject()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-max-notional-team";
        using var call = await OpenStreamAndDrainAckAsync(team, ct);

        // Threshold default: MaxOrderNotionalMwh = 50. QuantityScale.TicksPerUnit
        // = 10000, so qty_ticks = 60 * 10000 = 600_000 → 60 MWh > 50 MWh → reject.
        await call.RequestStream.WriteAsync(SubmitFor("H1", MarketProto.Side.Buy,
            priceTicks: 100, qtyTicks: 600_000), ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var resp = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
        Assert.Equal(StrategyProto.RejectReason.MaxNotional, resp.OrderReject.Reason);
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Submit_OrdersOver50_MaxOpenOrdersReject()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-max-open-orders-team";
        var consumer = _host.GetPrivateEventConsumer();

        using var call = await OpenStreamAndDrainAckAsync(team, ct);
        var clientId = _host.GetTeamState(team)!.ClientId;

        // Pre-populate 50 OrderAccepted entries on H1 — the next submit should
        // fail at MaxOpenOrdersGuard with cap 50.
        for (var i = 0; i < 50; i++)
        {
            await consumer.DispatchEnvelopeAsync(NewOrderAcceptedEnvelope(
                clientId, "H1", orderId: 50000 + i, priceTicks: 100, quantity: 1m), ct);
            // Drain the OrderAck pushed onto the team's outbound channel.
            Assert.True(await call.ResponseStream.MoveNext(ct));
        }

        // Submit #51 → MaxOpenOrdersGuard rejects.
        await call.RequestStream.WriteAsync(SubmitFor("H1", MarketProto.Side.Buy,
            priceTicks: 100, qtyTicks: 100), ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var resp = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
        Assert.Equal(StrategyProto.RejectReason.MaxOpenOrders, resp.OrderReject.Reason);
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Submit_PositionGrowsBeyond1000Mwh_MaxPositionReject()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-max-position-team";
        var consumer = _host.GetPrivateEventConsumer();

        using var call = await OpenStreamAndDrainAckAsync(team, ct);
        var clientId = _host.GetTeamState(team)!.ClientId;

        // Inject one OrderExecuted of 1000 MWh on H1 → net = +1000 MWh. Then a
        // submit of any positive qty on H1 would push hypothetical net > 1000 MWh.
        await consumer.DispatchEnvelopeAsync(NewOrderExecutedEnvelope(
            clientId, "H1", tradeId: 60001, orderId: 60001, priceTicks: 100, filledQty: 1000m), ct);
        // No OrderAck-on-fill — only Fill+Snapshot are pushed.
        Assert.True(await call.ResponseStream.MoveNext(ct));   // Fill
        Assert.True(await call.ResponseStream.MoveNext(ct));   // PositionSnapshot

        // Now submit any positive qty → hypothetical net = 1000.something MWh > 1000 cap.
        await call.RequestStream.WriteAsync(SubmitFor("H1", MarketProto.Side.Buy,
            priceTicks: 100, qtyTicks: 10), ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var resp = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
        Assert.Equal(StrategyProto.RejectReason.MaxPosition, resp.OrderReject.Reason);
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Submit_OwnSideCross_SelfTradeReject()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-self-trade-team";
        var consumer = _host.GetPrivateEventConsumer();

        using var call = await OpenStreamAndDrainAckAsync(team, ct);
        var clientId = _host.GetTeamState(team)!.ClientId;

        // Place a resting Sell at 100 via OrderAccepted injection (Side="Sell").
        await consumer.DispatchEnvelopeAsync(NewOrderAcceptedEnvelopeSide(
            clientId, "H1", orderId: 70001, priceTicks: 100, quantity: 1m, side: "Sell"), ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));   // OrderAck

        // Now submit a Buy at 200 → crosses our own resting Sell at 100.
        await call.RequestStream.WriteAsync(SubmitFor("H1", MarketProto.Side.Buy,
            priceTicks: 200, qtyTicks: 100), ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var resp = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
        Assert.Equal(StrategyProto.RejectReason.SelfTrade, resp.OrderReject.Reason);
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Submit_MsgRateOver500ps_RateLimited()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "guards-msg-rate-team";

        using var call = await OpenStreamAndDrainAckAsync(team, ct);
        var teamState = _host.GetTeamState(team)!;

        // Fast-fill MsgRateWindow with 501 entries to push over the 500/s threshold.
        // Doing it via real submits is feasible but slow; populate the queue directly
        // via the teamState.MsgRateWindow under StateLock — same effect, much faster.
        var now = DateTimeOffset.UtcNow;
        lock (teamState.StateLock)
        {
            for (var i = 0; i < 501; i++)
                teamState.MsgRateWindow.Enqueue(now);
        }

        await call.RequestStream.WriteAsync(SubmitFor("H1", MarketProto.Side.Buy,
            priceTicks: 100, qtyTicks: 100), ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var resp = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
        Assert.Equal(StrategyProto.RejectReason.RateLimited, resp.OrderReject.Reason);
        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Register_ReservedTeamName_StructuralRejectAndCloses()
    {
        // SPEC req 9 — reserved team_name "quoter" rejected at TeamRegistry. Already
        // covered by QuoterIdentityGuardTests; this fact double-binds the guard-chain
        // suite to the same surface (any code path that lands a frame in the chain
        // for "quoter" must end at structural reject).
        var ct = TestContext.Current.CancellationToken;
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = "quoter", LastSeenSequence = 0 },
        }, ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject,
            call.ResponseStream.Current.EventCase);
        Assert.Equal(StrategyProto.RejectReason.Structural,
            call.ResponseStream.Current.OrderReject.Reason);
        await call.RequestStream.CompleteAsync();
    }

    private async Task<Grpc.Core.AsyncDuplexStreamingCall<StrategyProto.StrategyCommand, StrategyProto.MarketEvent>>
        OpenStreamAndDrainAckAsync(string teamName, CancellationToken ct)
    {
        var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        var call = client.StreamStrategy(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = teamName, LastSeenSequence = 0 },
        }, ct);
        // Drain RegisterAck + 5 PositionSnapshot burst.
        for (var i = 0; i < 6; i++) Assert.True(await call.ResponseStream.MoveNext(ct));
        return call;
    }

    private static StrategyProto.StrategyCommand SubmitFor(string instrumentId, MarketProto.Side side,
        long priceTicks, long qtyTicks) => new()
    {
        OrderSubmit = new StrategyProto.OrderSubmit
        {
            ClientId = string.Empty,
            Instrument = NewInstrument(instrumentId),
            Side = side,
            OrderType = MarketProto.OrderType.Limit,
            PriceTicks = priceTicks,
            QuantityTicks = qtyTicks,
        },
    };

    private static MarketProto.Instrument NewInstrument(string id)
    {
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (start, end) = id switch
        {
            "H1" => (hourStart, hourStart.AddHours(1)),
            "Q1" => (hourStart, hourStart.AddMinutes(15)),
            "Q2" => (hourStart.AddMinutes(15), hourStart.AddMinutes(30)),
            "Q3" => (hourStart.AddMinutes(30), hourStart.AddMinutes(45)),
            "Q4" => (hourStart.AddMinutes(45), hourStart.AddHours(1)),
            _ => (hourStart, hourStart.AddHours(1)),
        };
        return new MarketProto.Instrument
        {
            InstrumentId = id,
            DeliveryArea = "DE",
            DeliveryPeriodStartNs = start.ToUnixTimeMilliseconds() * 1_000_000L,
            DeliveryPeriodEndNs = end.ToUnixTimeMilliseconds() * 1_000_000L,
            ProductType = MarketProto.ProductType.Unspecified,
        };
    }

    private static Envelope<JsonElement> NewOrderAcceptedEnvelope(
        string clientId, string instrumentId, long orderId, long priceTicks, decimal quantity) =>
        NewOrderAcceptedEnvelopeSide(clientId, instrumentId, orderId, priceTicks, quantity, "Buy");

    private static Envelope<JsonElement> NewOrderAcceptedEnvelopeSide(
        string clientId, string instrumentId, long orderId, long priceTicks, decimal quantity, string side)
    {
        var instrument = InstrumentForId(instrumentId);
        var dto = new OrderAcceptedEvent(
            OrderId: orderId,
            ClientId: clientId,
            InstrumentId: instrument,
            Side: side,
            OrderType: "Limit",
            PriceTicks: priceTicks,
            Quantity: quantity,
            DisplaySliceSize: null,
            TimestampNs: 0L);
        var payload = JsonSerializer.SerializeToElement(dto, JsonOptions);
        return new Envelope<JsonElement>(
            MessageType: MessageTypes.OrderAccepted,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            ClientId: clientId,
            InstrumentId: instrumentId,
            Sequence: orderId,
            Payload: payload);
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
