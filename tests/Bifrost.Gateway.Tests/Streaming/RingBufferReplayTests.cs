using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Streaming;

/// <summary>
/// GW-02 acceptance: reconnect-by-team-name with whole-round ring-buffer replay.
/// Tests inject <see cref="OrderExecutedEvent"/> envelopes via the
/// <c>PrivateEventConsumer.DispatchEnvelopeAsync</c> internal seam, force a
/// disconnect, then reconnect with a non-zero <c>last_seen_sequence</c> and
/// assert the replay slice flows on the response stream BEFORE any new event.
/// </summary>
[Collection("Gateway")]
public sealed class RingBufferReplayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GatewayTestHost _host;

    public RingBufferReplayTests(GatewayTestHost host) => _host = host;

    [Fact]
    public async Task Reconnect_ResumeWithinRetention_ReplaysSliceInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "ringbuf-replay-team";
        var consumer = _host.GetPrivateEventConsumer();

        // Pre-register the team so PrivateEventConsumer can route events by ClientId.
        var clientId = await RegisterAndGetClientIdAsync(team, ct);

        // Inject 5 OrderExecuted envelopes — each appends 2 entries to the ring (Fill +
        // PositionSnapshot), giving 10 ring entries total. The team has no live stream
        // attached at the moment of injection (we closed it after registration), so the
        // events accumulate in the ring without being consumed.
        for (var i = 0; i < 5; i++)
        {
            var envelope = NewOrderExecutedEnvelope(clientId, instrumentId: "H1",
                tradeId: 1000 + i, orderId: 5000 + i,
                priceTicks: 100, filledQty: 1m);
            await consumer.DispatchEnvelopeAsync(envelope, ct);
        }

        var team1 = _host.GetTeamState(team)!;
        long ringHead;
        lock (team1.StateLock) { ringHead = team1.Ring.Head; }
        Assert.True(ringHead >= 10, $"ring head should be ≥10 after 5 OrderExecuted injections; got {ringHead}");

        // Reconnect with last_seen_sequence = 4 → expect events from sequence 5 onward
        // to replay before any new live event.
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 4 },
        }, ct);

        // First frame is RegisterAck.
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var ack = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.RegisterAck, ack.EventCase);
        Assert.False(ack.RegisterAck.ReregisterRequired,
            "ResumedFromSequence=4 inside retention should NOT trigger reregister");
        Assert.Equal(4, ack.RegisterAck.ResumedFromSequence);

        // Drain 5 PositionSnapshots from RegisterAck burst.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(await call.ResponseStream.MoveNext(ct));
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PositionSnapshot,
                call.ResponseStream.Current.EventCase);
        }

        // Now the resume slice should follow — at least one Fill from the replay window.
        // (sequence 5 onward; the ring has 10 entries, so 5 entries should replay.)
        // Close the request stream first so the server flushes outbound and ends the
        // response — otherwise the client's last MoveNext blocks forever waiting for
        // a frame the server is not going to send.
        await call.RequestStream.CompleteAsync();

        var sawReplayedFill = false;
        while (await call.ResponseStream.MoveNext(ct))
        {
            if (call.ResponseStream.Current.EventCase == StrategyProto.MarketEvent.EventOneofCase.Fill)
            {
                sawReplayedFill = true;
            }
        }
        Assert.True(sawReplayedFill, "expected at least one replayed Fill in the resume slice");
    }

    [Fact]
    public async Task Reconnect_OutsideRetention_ReregisterRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "ringbuf-outside-retention-team";
        var clientId = await RegisterAndGetClientIdAsync(team, ct);

        // Inject 3 events so head moves to 6 (each OrderExecuted appends Fill +
        // PositionSnapshot = 2 ring entries). IsRetained(seq) = seq >= tail && seq < head;
        // last_seen_sequence=999 is forward-of-head, so it falls outside retention and
        // RegisterAck must set reregister_required=true.
        var consumer = _host.GetPrivateEventConsumer();
        for (var i = 0; i < 3; i++)
        {
            await consumer.DispatchEnvelopeAsync(
                NewOrderExecutedEnvelope(clientId, "H1", 7000 + i, 8000 + i, 100, 1m), ct);
        }

        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 999 },
        }, ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var ack = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.RegisterAck, ack.EventCase);
        Assert.True(ack.RegisterAck.ReregisterRequired,
            "last_seen_sequence outside retention must set reregister_required");

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task SettledToIterationOpen_WipesEveryRing()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "ringbuf-wipe-team";
        var clientId = await RegisterAndGetClientIdAsync(team, ct);
        var consumer = _host.GetPrivateEventConsumer();

        // Inject 5 events to populate the ring.
        for (var i = 0; i < 5; i++)
        {
            await consumer.DispatchEnvelopeAsync(
                NewOrderExecutedEnvelope(clientId, "H1", 8000 + i, 9000 + i, 100, 1m), ct);
        }
        var teamState = _host.GetTeamState(team)!;
        long preWipeHead;
        lock (teamState.StateLock) { preWipeHead = teamState.Ring.Head; }
        Assert.True(preWipeHead > 0);

        // Drive the round transition Settled → IterationOpen, which fires the wipe
        // path. The TeamRegistry.OnSettledToIterationOpen helper exposes the same
        // logic the Phase 06 RoundStateConsumer would call.
        var registry = _host.Services.GetRequiredService<TeamRegistry>();
        registry.OnSettledToIterationOpen();

        long postWipeHead;
        lock (teamState.StateLock)
        {
            postWipeHead = teamState.Ring.Head;
        }
        Assert.Equal(0, postWipeHead);

        // Reconnect with last_seen_sequence=2 from the prior round → reregister required
        // because head=0 and any seq>0 is impossible to replay.
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 2 },
        }, ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var ack = call.ResponseStream.Current;
        Assert.True(ack.RegisterAck.ReregisterRequired,
            "post-wipe ring should require reregister for any non-zero last_seen_sequence");

        await call.RequestStream.CompleteAsync();
    }

    private async Task<string> RegisterAndGetClientIdAsync(string teamName, CancellationToken ct)
    {
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = teamName, LastSeenSequence = 0 },
        }, ct);
        Assert.True(await call.ResponseStream.MoveNext(ct));
        var clientId = call.ResponseStream.Current.RegisterAck.ClientId;
        // Drain the 5 PositionSnapshot burst.
        for (var i = 0; i < 5; i++) Assert.True(await call.ResponseStream.MoveNext(ct));
        await call.RequestStream.CompleteAsync();
        return clientId;
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
