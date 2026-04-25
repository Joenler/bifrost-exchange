using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bifrost.Orchestrator.State;

/// <summary>
/// Persists <see cref="OrchestratorState"/> atomically via the
/// "write-to-.tmp + <c>File.Move</c>" pattern. On POSIX with source and
/// destination on the same filesystem, <c>File.Move(..., overwrite:true)</c>
/// issues a single <c>rename(2)</c> syscall - readers observe either the
/// old or the new file contents, never a torn / partial write.
/// </summary>
/// <remarks>
/// Cross-filesystem atomicity: the POSIX rename(2) guarantee requires
/// source (.tmp) and destination (final) to live on the same filesystem.
/// Operators deploying a persistent volume must mount the volume such
/// that both paths share a single mount point. The <see cref="StatePath"/>
/// + ".tmp" derivation keeps the .tmp file in the same directory as the
/// final file, which enforces this at the code level; a cross-FS deploy
/// surface would fall back to .NET's non-atomic CopyFile+DeleteFile path.
/// </remarks>
public sealed class JsonStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly IOptions<OrchestratorOptions> _opts;
    private readonly ILogger<JsonStateStore> _logger;

    public JsonStateStore(IOptions<OrchestratorOptions> opts, ILogger<JsonStateStore> logger)
    {
        _opts = opts;
        _logger = logger;
    }

    private string StatePath => _opts.Value.StatePath;

    private string TmpPath => StatePath + ".tmp";

    /// <summary>
    /// Write the state snapshot atomically. Throws on disk-full, permissions,
    /// or mount-evaporation failures - the orchestrator actor is expected
    /// to fail the originating command when Save throws, preserving the
    /// "orchestrator never transitions without persisting" invariant.
    /// </summary>
    public async ValueTask SaveAsync(OrchestratorState state, CancellationToken ct = default)
    {
        await using (FileStream fs = new(
            TmpPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(fs, state, JsonOpts, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        // Atomic rename on POSIX same-filesystem. On cross-filesystem the
        // .NET runtime falls back to CopyFile+DeleteFile which is NOT atomic;
        // see class-level remarks for the deployment caveat.
        File.Move(TmpPath, StatePath, overwrite: true);
    }

    /// <summary>
    /// Load the state snapshot. Returns null on missing file OR on corrupt
    /// JSON. Corrupt JSON is logged at Error level - operators must
    /// intervene (likely disk corruption, or an interrupted write from a
    /// previous session that leaked into the final path).
    /// </summary>
    public OrchestratorState? TryLoad()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            using FileStream fs = File.OpenRead(StatePath);
            return JsonSerializer.Deserialize<OrchestratorState>(fs, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "Orchestrator state file {Path} is invalid JSON - treating as fresh boot. "
                + "Manual inspection required.",
                StatePath);
            return null;
        }
    }
}
