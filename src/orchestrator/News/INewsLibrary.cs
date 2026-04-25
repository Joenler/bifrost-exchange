namespace Bifrost.Orchestrator.News;

/// <summary>
/// Seam over the canned news-library entries. The actor's event-emitting
/// command handler resolves a <c>NewsFireCmd.LibraryKey</c> via
/// <see cref="TryGet"/> and publishes the resulting <c>NewsPayload</c> +
/// optional <c>PhysicalShockPayload</c>. A follow-up plan ships the
/// filesystem-backed implementation; this plan supplies a
/// returns-null-for-every-key stub so the actor's DI graph compiles.
/// </summary>
public interface INewsLibrary
{
    /// <summary>
    /// Resolve a library key to its canned entry. Returns null when the key
    /// is not present in the library; callers convert that into a typed
    /// <c>McCommandResult { success=false, message="unknown library key: {key}" }</c>.
    /// </summary>
    NewsLibraryEntry? TryGet(string libraryKey);

    /// <summary>All known library keys, for audit / debugging surfaces.</summary>
    IReadOnlyCollection<string> Keys { get; }
}
