using Google.Protobuf;
using Xunit;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// CONT-07 row 5: bifrost.strategy.v1.OrderReject ↔ Bifrost.Contracts.Internal.Events.OrderRejectedEvent.
///
/// Proto OrderReject (client_order_id, reason, detail) and DTO OrderRejectedEvent
/// (order_id, client_id, reason, timestamp_ns) overlap only on the Reason enum.
/// DTO-only fields ride into ToInternal; proto-only fields ride into ToProto.
/// </summary>
public sealed class OrderRejectedTranslationTests
{
    [Fact]
    public void OrderReject_RoundTrips_ViaDto()
    {
        var original = new StrategyProto.OrderReject
        {
            ClientOrderId = "co-12345",
            Reason = StrategyProto.RejectReason.MaxNotional,
            Detail = "notional cap exceeded",
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(
            original,
            orderId: 999_111L,
            clientId: "client-001",
            timestampNs: 1_745_400_000_000_000_107L);

        var roundtrip = TranslationFixtures.ToProto(
            dto,
            clientOrderId: original.ClientOrderId,
            detail: original.Detail);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
