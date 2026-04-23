namespace Bifrost.Contracts.Internal.Commands;

/// <summary>
/// Trader-to-exchange command to cancel an existing order.
/// </summary>
public sealed record CancelOrderCommand(
    string ClientId,
    long OrderId,
    InstrumentIdDto InstrumentId);
