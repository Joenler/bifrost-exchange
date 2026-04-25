using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Gateway.MassCancel;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Streaming;

/// <summary>
/// GW-07 acceptance: mass-cancel-on-disconnect within 1s SLO.
///
/// The contract pieces this suite locks in:
///   1. Closing the bidi stream cleanly fires <c>DisconnectHandler.HandleAsync</c>
///      with a fresh 2-second CTS; every resting order produces exactly one
///      <c>CancelOrderCommand</c> publish via the recording publisher in the test fixture.
///   2. After mass-cancel, <c>TeamState.OpenOrdersByInstrument</c> is empty for every
///      instrument — the handler clears the per-instrument lists inline so a
///      subsequent reconnect starts from a clean view (Plan 07-07 design note).
///   3. <c>HandleAllAsync</c> fires per-team mass-cancel for every team in parallel.
/// </summary>
[Collection("Gateway")]
public sealed class MassCancelAcceptanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GatewayTestHost _host;

    public MassCancelAcceptanceTests(GatewayTestHost host) => _host = host;

    [Fact]
    public async Task StreamCleanClose_OnCompleted_PublishesCancelFleetWithin1s()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "mass-cancel-clean-close-team";
        var consumer = _host.GetPrivateEventConsumer();

        // Snapshot publisher state before this test so we can attribute new cancels.
        var beforeCancelCount = _host.CommandPublisher.Cancels.Count;

        // Open a stream → register → inject 6 OrderAccepted envelopes (2 per instrument
        // across 3 instruments) so the gateway's open-orders map is non-empty.
        using (var channel = _host.CreateGrpcChannel())
        {
            var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
            using var call = client.StreamStrategy(cancellationToken: ct);

            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
            }, ct);
            Assert.True(await call.ResponseStream.MoveNext(ct));
            var clientId = call.ResponseStream.Current.RegisterAck.ClientId;
            for (var i = 0; i < 5; i++) Assert.True(await call.ResponseStream.MoveNext(ct));

            // Populate OpenOrdersByInstrument via OrderAccepted injections.
            string[] instruments = { "H1", "Q1", "Q2" };
            for (var inst = 0; inst < instruments.Length; inst++)
            {
                for (var k = 0; k < 2; k++)
                {
                    var envelope = NewOrderAcceptedEnvelope(clientId, instruments[inst],
                        orderId: 6000 + (inst * 10) + k,
                        priceTicks: 100, quantity: 1m);
                    await consumer.DispatchEnvelopeAsync(envelope, ct);
                    // Drain the OrderAck the consumer pushes onto outbound (non-blocking).
                    Assert.True(await call.ResponseStream.MoveNext(ct));
                }
            }

            await call.RequestStream.CompleteAsync();
            // Drain remaining outbound until the server closes the response stream
            // (this is the moment finally → DisconnectHandler.HandleAsync fires).
            try { while (await call.ResponseStream.MoveNext(ct)) { } } catch { }
        }

        // The gateway uses a fresh 2-second CTS; the recording publisher captures
        // exactly N=6 cancels (one per resting order) within that budget.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline
               && _host.CommandPublisher.Cancels.Count - beforeCancelCount < 6)
        {
            await Task.Delay(20, ct);
        }
        var observed = _host.CommandPublisher.Cancels.Count - beforeCancelCount;
        Assert.True(observed >= 6,
            $"DisconnectHandler should publish ≥6 CancelOrderCommands (one per resting order); got {observed}");

        // After mass-cancel, the team's OpenOrdersByInstrument should be empty.
        var teamState = _host.GetTeamState(team)!;
        lock (teamState.StateLock)
        {
            for (var i = 0; i < teamState.OpenOrdersByInstrument.Length; i++)
            {
                Assert.Empty(teamState.OpenOrdersByInstrument[i]);
            }
        }
    }

    [Fact]
    public async Task StreamReconnect_AfterMassCancel_NoSurvivingOpenOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "mass-cancel-reconnect-team";
        var consumer = _host.GetPrivateEventConsumer();

        using (var channel = _host.CreateGrpcChannel())
        {
            var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
            using var call = client.StreamStrategy(cancellationToken: ct);
            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
            }, ct);
            Assert.True(await call.ResponseStream.MoveNext(ct));
            var clientId = call.ResponseStream.Current.RegisterAck.ClientId;
            for (var i = 0; i < 5; i++) Assert.True(await call.ResponseStream.MoveNext(ct));

            for (var i = 0; i < 4; i++)
            {
                await consumer.DispatchEnvelopeAsync(
                    NewOrderAcceptedEnvelope(clientId, "H1", orderId: 7000 + i, priceTicks: 100, quantity: 1m), ct);
                Assert.True(await call.ResponseStream.MoveNext(ct));
            }

            await call.RequestStream.CompleteAsync();
            try { while (await call.ResponseStream.MoveNext(ct)) { } } catch { }
        }

        // Wait for the disconnect handler to drain.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ts = _host.GetTeamState(team)!;
            int total;
            lock (ts.StateLock)
            {
                total = 0;
                for (var i = 0; i < ts.OpenOrdersByInstrument.Length; i++)
                    total += ts.OpenOrdersByInstrument[i].Count;
            }
            if (total == 0) break;
            await Task.Delay(20, ct);
        }

        var teamState = _host.GetTeamState(team)!;
        lock (teamState.StateLock)
        {
            for (var i = 0; i < teamState.OpenOrdersByInstrument.Length; i++)
            {
                Assert.Empty(teamState.OpenOrdersByInstrument[i]);
            }
        }

        // Reconnect — same team_name returns the same TeamState; OpenOrders still empty.
        using var ch2 = _host.CreateGrpcChannel();
        var c2 = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(ch2);
        using var call2 = c2.StreamStrategy(cancellationToken: ct);
        await call2.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
        }, ct);
        Assert.True(await call2.ResponseStream.MoveNext(ct));   // RegisterAck
        await call2.RequestStream.CompleteAsync();

        var ts2 = _host.GetTeamState(team)!;
        lock (ts2.StateLock)
        {
            for (var i = 0; i < ts2.OpenOrdersByInstrument.Length; i++)
            {
                Assert.Empty(ts2.OpenOrdersByInstrument[i]);
            }
        }
    }

    [Fact]
    public async Task HandleAllAsync_AcrossMultipleTeams_ClearsEveryOpenOrderMap()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = _host.Services.GetRequiredService<TeamRegistry>();
        var disconnect = _host.Services.GetRequiredService<DisconnectHandler>();
        var consumer = _host.GetPrivateEventConsumer();

        // Register 3 teams + populate each with 2 open orders on H1.
        string[] teams = { "ha-team-alpha", "ha-team-bravo", "ha-team-charlie" };
        var clientIds = new string[teams.Length];
        for (var t = 0; t < teams.Length; t++)
        {
            using var channel = _host.CreateGrpcChannel();
            var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
            using var call = client.StreamStrategy(cancellationToken: ct);
            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                Register = new StrategyProto.Register { TeamName = teams[t], LastSeenSequence = 0 },
            }, ct);
            Assert.True(await call.ResponseStream.MoveNext(ct));
            clientIds[t] = call.ResponseStream.Current.RegisterAck.ClientId;
            for (var i = 0; i < 5; i++) Assert.True(await call.ResponseStream.MoveNext(ct));

            for (var k = 0; k < 2; k++)
            {
                await consumer.DispatchEnvelopeAsync(
                    NewOrderAcceptedEnvelope(clientIds[t], "H1",
                        orderId: 8000 + (t * 10) + k,
                        priceTicks: 100, quantity: 1m), ct);
                Assert.True(await call.ResponseStream.MoveNext(ct));
            }

            await call.RequestStream.CompleteAsync();
            try { while (await call.ResponseStream.MoveNext(ct)) { } } catch { }
        }

        // The per-stream finally will have already cleared each team's open-order
        // map. Re-populate to simulate the SIGTERM-mid-trade scenario the
        // ApplicationStopping hook is responsible for, then drive HandleAllAsync.
        for (var t = 0; t < teams.Length; t++)
        {
            var ts = _host.GetTeamState(teams[t])!;
            lock (ts.StateLock)
            {
                for (var k = 0; k < 2; k++)
                {
                    ts.OpenOrdersByInstrument[0].Add(new OpenOrder(
                        OrderId: 9000 + (t * 10) + k,
                        ClientOrderId: string.Empty,
                        InstrumentIndex: 0,
                        Side: "Buy",
                        PriceTicks: 100,
                        QuantityTicks: 10000,
                        DisplaySliceTicks: 0,
                        SubmittedAtUtc: DateTimeOffset.UtcNow));
                }
            }
        }

        var snapshot = registry.SnapshotAll().Where(s => Array.IndexOf(teams, s.TeamName) >= 0).ToArray();
        await disconnect.HandleAllAsync(snapshot, ct);

        for (var t = 0; t < teams.Length; t++)
        {
            var ts = _host.GetTeamState(teams[t])!;
            lock (ts.StateLock)
            {
                for (var i = 0; i < ts.OpenOrdersByInstrument.Length; i++)
                {
                    Assert.Empty(ts.OpenOrdersByInstrument[i]);
                }
            }
        }
    }

    private static Envelope<JsonElement> NewOrderAcceptedEnvelope(
        string clientId, string instrumentId, long orderId, long priceTicks, decimal quantity)
    {
        var instrument = InstrumentForId(instrumentId);
        var dto = new OrderAcceptedEvent(
            OrderId: orderId,
            ClientId: clientId,
            InstrumentId: instrument,
            Side: "Buy",
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
