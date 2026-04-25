using Bifrost.Gateway.Tests.Fixtures;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Streaming;

/// <summary>
/// SPEC req 1 + 2 + 7: first-frame Register handshake, RegisterAck-then-burst
/// shape, reconnect-by-team-name returns the same ClientId.
/// </summary>
[Collection("Gateway")]
public sealed class RegisterHandshakeTests
{
    private readonly GatewayTestHost _host;

    public RegisterHandshakeTests(GatewayTestHost host) => _host = host;

    [Fact]
    public async Task FirstFrame_NotRegister_StreamCloses_NoReply()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        // Send an OrderSubmit as the first frame — SPEC req 1: server must close
        // with NO REPLY because the first frame must be Register.
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            OrderSubmit = new StrategyProto.OrderSubmit
            {
                ClientId = "anyone",
                Instrument = NewInstrument("H1"),
                Side = MarketProto.Side.Buy,
                OrderType = MarketProto.OrderType.Limit,
                PriceTicks = 100,
                QuantityTicks = 1,
            },
        }, ct);
        await call.RequestStream.CompleteAsync();

        // Server returns no frames.
        Assert.False(await call.ResponseStream.MoveNext(ct));
    }

    [Fact]
    public async Task Register_ValidTeamName_RegisterAckIsFirstFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = "alpha", LastSeenSequence = 0 },
        }, ct);

        Assert.True(await call.ResponseStream.MoveNext(ct));
        var first = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.RegisterAck, first.EventCase);
        Assert.NotEmpty(first.RegisterAck.ClientId);

        // D-06a: 5 PositionSnapshots in canonical instrument order follow.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(await call.ResponseStream.MoveNext(ct));
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.PositionSnapshot, call.ResponseStream.Current.EventCase);
        }

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Register_ReconnectSameName_ReturnsSameClientId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);

        string firstClientId;
        using (var call1 = client.StreamStrategy(cancellationToken: ct))
        {
            await call1.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                Register = new StrategyProto.Register { TeamName = "bravo", LastSeenSequence = 0 },
            }, ct);
            await call1.ResponseStream.MoveNext(ct);
            firstClientId = call1.ResponseStream.Current.RegisterAck.ClientId;
            await call1.RequestStream.CompleteAsync();
        }

        using var call2 = client.StreamStrategy(cancellationToken: ct);
        await call2.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = "bravo", LastSeenSequence = 0 },
        }, ct);
        await call2.ResponseStream.MoveNext(ct);
        Assert.Equal(firstClientId, call2.ResponseStream.Current.RegisterAck.ClientId);
        await call2.RequestStream.CompleteAsync();
    }

    private static MarketProto.Instrument NewInstrument(string id) => new()
    {
        InstrumentId = id,
        DeliveryArea = "DE",
        DeliveryPeriodStartNs = 0L,
        DeliveryPeriodEndNs = 0L,
        ProductType = MarketProto.ProductType.Unspecified,
    };
}
