using RabbitMQ.Client;

namespace Bifrost.Exchange.Infrastructure.RabbitMq;

public static class RabbitMqTopology
{
    public const string CommandExchange = "bifrost.cmd";
    public const string PublicExchange = "bifrost.public";
    public const string PrivateExchange = "bifrost.private";

    public const string CommandQueue = "bifrost.cmd.v1";

    public const string RoutingKeyOrderSubmit = "cmd.order.submit";
    public const string RoutingKeyOrderCancel = "cmd.order.cancel";
    public const string RoutingKeyOrderReplace = "cmd.order.replace";
    public const string RoutingKeyInquiryBook = "cmd.inquiry.book";
    public const string RoutingKeyClientSubscribe = "cmd.client.subscribe";

    public const string PublicInstrumentAvailableRoutingKey = "public.instruments.available";

    public static string PrivateQueueName(string clientId) => $"bifrost.private.v1.{clientId}";
    public static string PublicDeltaRoutingKey(string instrumentId) => $"public.book.delta.{instrumentId}";
    public static string PublicTradeRoutingKey(string instrumentId) => $"public.trade.{instrumentId}";
    public static string PublicSnapshotRoutingKey(string instrumentId) => $"public.book.snapshot.{instrumentId}";
    public static string PrivateOrderRoutingKey(string clientId, string eventType) =>
        $"private.order.{clientId}.{eventType}";
    public static string PrivateExecRoutingKey(string clientId, string eventType) =>
        $"private.exec.{clientId}.{eventType}";

    public static async Task DeclareExchangeTopologyAsync(IChannel channel, CancellationToken ct = default)
    {
        await channel.ExchangeDeclareAsync(CommandExchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(PublicExchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(PrivateExchange, ExchangeType.Topic, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync(CommandQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

        await channel.QueueBindAsync(CommandQueue, CommandExchange, "cmd.#", cancellationToken: ct);
    }
}
