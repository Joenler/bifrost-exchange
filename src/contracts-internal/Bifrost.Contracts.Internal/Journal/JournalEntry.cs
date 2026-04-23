namespace Bifrost.Contracts.Internal.Journal;

/// <summary>
/// Append-only journal entry wrapping a serialized command payload.
/// Each entry represents one command accepted by the matching engine,
/// stored with its sequence position for deterministic replay.
/// </summary>
public sealed record JournalEntry(
    long SequenceNumber,
    string EventType,
    byte[] Payload,
    long TimestampNs,
    int SchemaVersion);
