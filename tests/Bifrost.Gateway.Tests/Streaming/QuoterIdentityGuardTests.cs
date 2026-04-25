using Bifrost.Gateway.Tests.Fixtures;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Streaming;

/// <summary>
/// SPEC req 9: reserved-id rejection at the gRPC boundary. Two paths covered:
///   1. Register frame's <c>team_name</c> matches "quoter" / "dah-auction"
///      (case-insensitive) → TeamRegistry rejects → gateway emits
///      OrderReject(STRUCTURAL).
///   2. Mid-stream OrderSubmit's embedded <c>client_id</c> equals "quoter" →
///      StructuralGuard rejects (defence-in-depth on top of InboundTranslator's
///      boundary check).
/// </summary>
[Collection("Gateway")]
public sealed class QuoterIdentityGuardTests
{
    private readonly GatewayTestHost _host;

    public QuoterIdentityGuardTests(GatewayTestHost host) => _host = host;

    [Theory]
    [InlineData("quoter")]
    [InlineData("QUOTER")]
    [InlineData("Quoter")]
    [InlineData("dah-auction")]
    public async Task RegisterAsReservedName_RejectedWithStructural(string reservedName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = reservedName, LastSeenSequence = 0 },
        }, ct);

        Assert.True(await call.ResponseStream.MoveNext(ct));
        var first = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, first.EventCase);
        Assert.Equal(StrategyProto.RejectReason.Structural, first.OrderReject.Reason);

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task SubmitWithEmbeddedQuoterClientId_RejectedAtBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        // Register as a non-reserved team to get past the handshake.
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = "innocent", LastSeenSequence = 0 },
        }, ct);
        // Drain RegisterAck + 5 PositionSnapshots.
        for (var i = 0; i < 6; i++)
        {
            Assert.True(await call.ResponseStream.MoveNext(ct));
        }

        // Now send an OrderSubmit with embedded ClientId == "quoter" — StructuralGuard
        // catches this at Tier 1 with REJECT_REASON_STRUCTURAL.
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            OrderSubmit = new StrategyProto.OrderSubmit
            {
                ClientId = "quoter",
                Instrument = NewInstrument("H1"),
                Side = MarketProto.Side.Buy,
                OrderType = MarketProto.OrderType.Limit,
                PriceTicks = 100,
                QuantityTicks = 1,
            },
        }, ct);

        Assert.True(await call.ResponseStream.MoveNext(ct));
        var resp = call.ResponseStream.Current;
        Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
        Assert.Equal(StrategyProto.RejectReason.Structural, resp.OrderReject.Reason);

        await call.RequestStream.CompleteAsync();
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
