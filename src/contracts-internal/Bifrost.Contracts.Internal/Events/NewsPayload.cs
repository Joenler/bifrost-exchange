namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Wire DTO for publications on bifrost.events.v1/events.news. Severity values
/// are "info" / "warn" / "urgent" (matches events.proto::Severity enum). When
/// LibraryKey is the empty string, the command was NewsPublishCmd (free-text);
/// otherwise it was NewsFireCmd (canned library hit).
/// </summary>
public sealed record NewsPayload(
    string Text,
    string LibraryKey,
    string Severity);
