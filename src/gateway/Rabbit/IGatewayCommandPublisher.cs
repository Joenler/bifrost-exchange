using Bifrost.Contracts.Internal.Commands;

namespace Bifrost.Gateway.Rabbit;

/// <summary>
/// Abstraction for the gateway's inbound-command RabbitMQ publisher. The live
/// implementation (<see cref="GatewayCommandPublisher"/>) owns a dedicated
/// <c>RabbitMQ.Client.IChannel</c> per Pitfall 6 — the channel is constructed
/// in DI and never shared with consumers. Tests substitute a recording stub via
/// <c>WebApplicationFactory.ConfigureTestServices</c> so the in-process
/// <c>StrategyGatewayService</c> integration tests need no real RabbitMQ.
/// </summary>
public interface IGatewayCommandPublisher
{
    ValueTask PublishSubmitOrderAsync(string clientId, SubmitOrderCommand cmd, string correlationId, CancellationToken ct = default);
    ValueTask PublishCancelOrderAsync(string clientId, CancelOrderCommand cmd, string correlationId, CancellationToken ct = default);
    ValueTask PublishReplaceOrderAsync(string clientId, ReplaceOrderCommand cmd, string correlationId, CancellationToken ct = default);
}
