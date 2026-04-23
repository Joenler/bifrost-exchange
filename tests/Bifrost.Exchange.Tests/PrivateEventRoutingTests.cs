using Bifrost.Exchange.Infrastructure.RabbitMq;
using Xunit;

namespace Bifrost.Exchange.Tests;

/// <summary>
/// EX-04 coverage: per-team private execution reports over RabbitMQ with
/// typed wire contracts. Asserts the 4 exchange/queue-name constants +
/// PrivateQueueName template rename to bifrost.* landed, AND that routing
/// keys stayed Arena-shape byte-identical (D-03 + RESEARCH Pitfall 5).
///
/// Covers the topology-level contract that RabbitMqEventPublisher relies
/// on when dispatching PublishPrivate -> PrivateOrderRoutingKey(clientId, eventType).
/// </summary>
public sealed class PrivateEventRoutingTests
{
    [Fact]
    public void Topology_CommandExchange_UsesBifrostPrefix()
        => Assert.Equal("bifrost.cmd", RabbitMqTopology.CommandExchange);

    [Fact]
    public void Topology_PublicExchange_UsesBifrostPrefix()
        => Assert.Equal("bifrost.public", RabbitMqTopology.PublicExchange);

    [Fact]
    public void Topology_PrivateExchange_UsesBifrostPrefix()
        => Assert.Equal("bifrost.private", RabbitMqTopology.PrivateExchange);

    [Fact]
    public void Topology_CommandQueue_UsesBifrostPrefix()
        => Assert.Equal("bifrost.cmd.v1", RabbitMqTopology.CommandQueue);

    [Fact]
    public void Topology_PrivateQueueName_MatchesBifrostTemplate()
        => Assert.Equal("bifrost.private.v1.team-alpha", RabbitMqTopology.PrivateQueueName("team-alpha"));

    [Theory]
    [InlineData("team-alpha", "accepted", "private.order.team-alpha.accepted")]
    [InlineData("team-beta",  "rejected", "private.order.team-beta.rejected")]
    [InlineData("team-gamma", "cancelled", "private.order.team-gamma.cancelled")]
    public void Topology_PrivateOrderRoutingKey_StaysArenaShape(string clientId, string eventType, string expected)
        => Assert.Equal(expected, RabbitMqTopology.PrivateOrderRoutingKey(clientId, eventType));

    [Theory]
    [InlineData("team-alpha", "fill", "private.exec.team-alpha.fill")]
    [InlineData("team-beta",  "done", "private.exec.team-beta.done")]
    public void Topology_PrivateExecRoutingKey_StaysArenaShape(string clientId, string eventType, string expected)
        => Assert.Equal(expected, RabbitMqTopology.PrivateExecRoutingKey(clientId, eventType));

    [Fact]
    public void Topology_PublicDeltaRoutingKey_StartsWithArenaPrefix()
        => Assert.Equal(
            "public.book.delta.DE-HOUR-20260101T0000",
            RabbitMqTopology.PublicDeltaRoutingKey("DE-HOUR-20260101T0000"));

    [Fact]
    public void Topology_PublicTradeRoutingKey_StartsWithArenaPrefix()
        => Assert.Equal(
            "public.trade.DE-HOUR-20260101T0000",
            RabbitMqTopology.PublicTradeRoutingKey("DE-HOUR-20260101T0000"));

    [Fact]
    public void Topology_PublicSnapshotRoutingKey_StartsWithArenaPrefix()
        => Assert.Equal(
            "public.book.snapshot.DE-HOUR-20260101T0000",
            RabbitMqTopology.PublicSnapshotRoutingKey("DE-HOUR-20260101T0000"));

    [Fact]
    public void Topology_RoutingKeysDoNotLeakBifrostPrefix()
    {
        // Pitfall 5 invariant: the bifrost.* prefix is ONLY for exchange + queue
        // names, NEVER for routing-key strings. Cross-contamination would break
        // the AMQP topology bindings. Assertions target the const routing keys.
        Assert.DoesNotContain("bifrost.", RabbitMqTopology.RoutingKeyOrderSubmit);
        Assert.DoesNotContain("bifrost.", RabbitMqTopology.RoutingKeyOrderCancel);
        Assert.DoesNotContain("bifrost.", RabbitMqTopology.RoutingKeyOrderReplace);
        Assert.DoesNotContain("bifrost.", RabbitMqTopology.RoutingKeyInquiryBook);
        Assert.DoesNotContain("bifrost.", RabbitMqTopology.RoutingKeyClientSubscribe);
        Assert.DoesNotContain("bifrost.", RabbitMqTopology.PublicInstrumentAvailableRoutingKey);
    }

    [Fact]
    public void Topology_RoutingKeysPreserveCmdPrefix()
    {
        Assert.Equal("cmd.order.submit", RabbitMqTopology.RoutingKeyOrderSubmit);
        Assert.Equal("cmd.order.cancel", RabbitMqTopology.RoutingKeyOrderCancel);
        Assert.Equal("cmd.order.replace", RabbitMqTopology.RoutingKeyOrderReplace);
        Assert.Equal("cmd.inquiry.book", RabbitMqTopology.RoutingKeyInquiryBook);
        Assert.Equal("cmd.client.subscribe", RabbitMqTopology.RoutingKeyClientSubscribe);
    }
}
