namespace Bifrost.Contracts.Internal.Commands;

/// <summary>
/// Trader-to-exchange request for a full order book snapshot.
/// </summary>
public sealed record GetBookSnapshotRequest(
    string ClientId,
    InstrumentIdDto InstrumentId);
