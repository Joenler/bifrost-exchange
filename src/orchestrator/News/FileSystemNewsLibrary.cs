using Microsoft.Extensions.Options;

namespace Bifrost.Orchestrator.News;

/// <summary>
/// Stub shell for the actor-loop wiring plan. A follow-up plan fills in the
/// JSON-backed loader (FrozenDictionary snapshot at startup, read from
/// <c>OrchestratorOptions.NewsLibraryPath</c>). Public surface is LOCKED —
/// the follow-up plan MUST NOT change it.
/// </summary>
public sealed class FileSystemNewsLibrary : INewsLibrary
{
    public FileSystemNewsLibrary(IOptions<OrchestratorOptions> opts)
    {
        // Options are observed here so the field is non-idle under the
        // returns-null stub; the follow-up plan reads opts.Value.NewsLibraryPath.
        _ = opts.Value.NewsLibraryPath;
    }

    public NewsLibraryEntry? TryGet(string libraryKey) => null;

    public IReadOnlyCollection<string> Keys => Array.Empty<string>();
}
