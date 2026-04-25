using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bifrost.Orchestrator.News;

/// <summary>
/// JSON-backed canned news library. Reads
/// <see cref="OrchestratorOptions.NewsLibraryPath"/> once at construction time
/// (DI singleton lifetime) and freezes the parsed dictionary for O(1) lookups.
/// No hot-reload — operator edits to the JSON file require an orchestrator
/// restart to take effect.
/// </summary>
/// <remarks>
/// Public surface is identical to the stub shipped earlier in this phase: the
/// constructor signature did not change, <see cref="INewsLibrary.TryGet"/> and
/// <see cref="INewsLibrary.Keys"/> still match the interface verbatim. Tests
/// that previously substituted <c>EmptyNewsLibrary</c> can now point this
/// implementation at a fixture JSON without altering the actor's constructor.
///
/// Path-missing fallback: when the configured path does not exist (operator
/// removed the file, smoke env without the bind-mount, etc.), the library
/// loads as an empty dictionary instead of throwing — TryGet returns null for
/// every key and the actor surfaces the standard "unknown library key"
/// rejection. This matches the "fail-soft on optional config" posture used
/// for the other operator-editable inputs (hackathon.json, scenarios).
///
/// JSON-deserializer options match the camelCase wire convention used
/// elsewhere in the orchestrator: trailing commas allowed, line/block comments
/// silently skipped so operators can annotate the file in-place.
/// </remarks>
public sealed class FileSystemNewsLibrary : INewsLibrary
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly FrozenDictionary<string, NewsLibraryEntry> _entries;

    public FileSystemNewsLibrary(IOptions<OrchestratorOptions> opts)
    {
        string path = opts.Value.NewsLibraryPath;
        if (!File.Exists(path))
        {
            _entries = FrozenDictionary<string, NewsLibraryEntry>.Empty;
            return;
        }

        using FileStream fs = File.OpenRead(path);
        Dictionary<string, NewsLibraryEntry>? parsed =
            JsonSerializer.Deserialize<Dictionary<string, NewsLibraryEntry>>(fs, JsonOpts);
        _entries = (parsed ?? new()).ToFrozenDictionary();
    }

    public NewsLibraryEntry? TryGet(string libraryKey) =>
        _entries.TryGetValue(libraryKey, out NewsLibraryEntry? entry) ? entry : null;

    public IReadOnlyCollection<string> Keys => _entries.Keys;
}
