namespace Bifrost.Orchestrator.News;

/// <summary>
/// Shape mirrors ADR-0005 canned news-library format. <see cref="Shock"/> is
/// optional — null means a flavor-only news item with no physical shock.
/// </summary>
public sealed record NewsLibraryEntry(
    string Text,
    string Severity,
    NewsLibraryShock? Shock);

/// <summary>
/// Optional physical-shock payload attached to a news-library entry. When
/// present, the actor publishes a <c>PhysicalShockPayload</c> on
/// <c>events.physical_shock</c> alongside the <c>NewsPayload</c> on
/// <c>events.news</c>.
/// </summary>
public sealed record NewsLibraryShock(
    int Mw,
    string Label,
    string Persistence);
