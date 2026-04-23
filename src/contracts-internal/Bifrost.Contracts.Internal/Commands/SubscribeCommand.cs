namespace Bifrost.Contracts.Internal.Commands;

/// <summary>
/// Trader-to-exchange command to subscribe to market data for a client session.
/// </summary>
public sealed record SubscribeCommand(string ClientId);
