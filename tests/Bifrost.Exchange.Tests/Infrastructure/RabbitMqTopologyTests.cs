using Bifrost.Exchange.Infrastructure.RabbitMq;
using Xunit;

namespace Bifrost.Exchange.Tests.Infrastructure;

/// <summary>
/// Asserts the four imbalance-simulator routing-key members added to
/// <see cref="RabbitMqTopology"/> return their documented wire strings.
/// Every downstream simulator publisher / consumer references these
/// members by name -- if one of them drifts, all bindings break in
/// lockstep, so a pinning test up front is the cheap fix.
/// </summary>
public sealed class RabbitMqTopologyTests
{
    [Fact]
    public void PublicForecastRoutingKey_IsStableConstant()
    {
        Assert.Equal("public.forecast", RabbitMqTopology.PublicForecastRoutingKey);
    }

    [Fact]
    public void PublicForecastRevisionRoutingKey_IsStableConstant()
    {
        Assert.Equal("public.forecast.revision", RabbitMqTopology.PublicForecastRevisionRoutingKey);
    }

    [Theory]
    [InlineData("DE-20260101T0000-Q1", "public.imbalance.print.DE-20260101T0000-Q1")]
    [InlineData("DE-20260101T0000-Q2", "public.imbalance.print.DE-20260101T0000-Q2")]
    [InlineData("DE-20260101T0000-H",  "public.imbalance.print.DE-20260101T0000-H")]
    public void PublicImbalancePrintRoutingKey_EmbedsInstrumentId(string instrumentId, string expected)
    {
        Assert.Equal(expected, RabbitMqTopology.PublicImbalancePrintRoutingKey(instrumentId));
    }

    [Theory]
    [InlineData("alpha",  "private.imbalance.settlement.alpha")]
    [InlineData("bravo",  "private.imbalance.settlement.bravo")]
    [InlineData("quoter", "private.imbalance.settlement.quoter")]
    public void PrivateImbalanceSettlementRoutingKey_EmbedsClientId(string clientId, string expected)
    {
        // Topology is client-agnostic; the downstream simulator filters
        // which client ids actually receive settlement rows.
        Assert.Equal(expected, RabbitMqTopology.PrivateImbalanceSettlementRoutingKey(clientId));
    }
}
