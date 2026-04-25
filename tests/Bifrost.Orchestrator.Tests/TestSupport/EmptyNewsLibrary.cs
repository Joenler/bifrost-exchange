using Bifrost.Orchestrator.News;

namespace Bifrost.Orchestrator.Tests.TestSupport;

/// <summary>
/// Zero-behaviour <see cref="INewsLibrary"/> test double. Used by the actor-loop
/// stress, crash-restart, and abort-flow tests in this plan - none of them
/// exercise the NewsFire path, so returns-null-for-every-key suffices. A
/// follow-up plan's tests use <see cref="FileSystemNewsLibrary"/> against a real
/// fixture JSON.
/// </summary>
public sealed class EmptyNewsLibrary : INewsLibrary
{
    public NewsLibraryEntry? TryGet(string libraryKey) => null;

    public IReadOnlyCollection<string> Keys => Array.Empty<string>();
}
