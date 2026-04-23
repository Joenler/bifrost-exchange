namespace Bifrost.Contracts.Internal;

/// <summary>
/// Message envelope wrapping a typed payload with routing metadata and sequence tracking.
/// </summary>
public sealed record Envelope<T>(
    string MessageType,
    DateTimeOffset TimestampUtc,
    string? CorrelationId,
    string? ClientId,
    string? InstrumentId,
    long? Sequence,
    T Payload);
