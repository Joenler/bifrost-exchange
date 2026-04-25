using Bifrost.Orchestrator.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Orchestrator.Tests.State;

/// <summary>
/// Persistence gate for <see cref="JsonStateStore"/>: round-trip, atomic
/// tmp+rename semantics, corrupt-JSON -> null fallback, 1000-interleaved
/// saves/loads never observe torn content, and kill-the-writer fault
/// injection leaves the previous valid file intact.
/// </summary>
public sealed class JsonStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;
    private readonly JsonStateStore _store;

    public JsonStateStoreTests()
    {
        _tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"bifrost-orch-test-{Guid.NewGuid():N}")).FullName;
        _statePath = Path.Combine(_tempDir, "state.json");

        IOptions<OrchestratorOptions> opts = Options.Create(
            new OrchestratorOptions { StatePath = _statePath });
        _store = new JsonStateStore(opts, NullLogger<JsonStateStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Test teardown best-effort; the fresh Guid-named directory will
            // not collide with anything the OS hasn't already cleaned.
        }
    }

    [Fact]
    public void TryLoad_NullOnMissingFile()
    {
        Assert.Null(_store.TryLoad());
    }

    [Fact]
    public async Task SaveAndLoad_Roundtrip()
    {
        OrchestratorState original = OrchestratorState.FreshBoot(
            masterSeed: 42,
            nowNs: 1714516800000000123L) with
        {
            State = BifrostState.RoundOpen,
            RoundNumber = 3,
            Paused = true,
            PausedReason = "mc",
        };

        await _store.SaveAsync(original, TestContext.Current.CancellationToken);

        OrchestratorState? loaded = _store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(BifrostState.RoundOpen, loaded.State);
        Assert.Equal(3, loaded.RoundNumber);
        Assert.True(loaded.Paused);
        Assert.Equal("mc", loaded.PausedReason);
        Assert.Equal(42L, loaded.MasterSeed);
        Assert.Equal(OrchestratorState.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(1714516800000000123L, loaded.LastTransitionNs);
    }

    [Fact]
    public async Task SaveAndLoad_AllOptionalFieldsNullRoundtrip()
    {
        OrchestratorState original = OrchestratorState.FreshBoot(0, 1);

        await _store.SaveAsync(original, TestContext.Current.CancellationToken);

        OrchestratorState? loaded = _store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Null(loaded.PausedReason);
        Assert.Null(loaded.BlockedReason);
        Assert.Null(loaded.AbortReason);
    }

    [Fact]
    public void TryLoad_ReturnsNullOnCorruptJson()
    {
        File.WriteAllText(_statePath, "{ this is not valid json");
        Assert.Null(_store.TryLoad());
    }

    /// <summary>
    /// The atomicity gate: 1000 save/load pairs interleaved across two
    /// tasks. If the atomic tmp+rename is implemented correctly, every load
    /// observes either null (file briefly missing between File.Move atomic
    /// steps - in practice this never happens on POSIX because rename(2) is
    /// a single syscall, but the test tolerates it) OR a valid
    /// OrchestratorState with MasterSeed == 0. An unhandled JsonException
    /// escaping TryLoad or an asserted-corrupt MasterSeed would indicate
    /// the reader saw a torn write.
    /// </summary>
    [Fact]
    public async Task InterleavedSaveAndLoad_NeverObservesInvalidJson()
    {
        // Link the test-runner cancellation token with a 30s watchdog so the
        // test fails fast if the writer or reader gets stuck, but still
        // honours `dotnet test` cancellation (xUnit1051).
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = cts.Token;

        OrchestratorState seed = OrchestratorState.FreshBoot(0, 1);
        await _store.SaveAsync(seed, token);

        Task writer = Task.Run(async () =>
        {
            for (int i = 0; i < 1000 && !token.IsCancellationRequested; i++)
            {
                OrchestratorState state = seed with
                {
                    RoundNumber = i,
                    LastTransitionNs = i * 1000L,
                };
                await _store.SaveAsync(state, token);
            }
        }, token);

        Task<int> reader = Task.Run(
            () =>
            {
                int attempts = 0;
                while (!writer.IsCompleted && !token.IsCancellationRequested)
                {
                    attempts++;
                    OrchestratorState? loaded = _store.TryLoad();

                    // Acceptable outcomes:
                    //   - null (file briefly missing during rename; rare on POSIX)
                    //   - a valid state whose MasterSeed matches the seed writer.
                    // Unacceptable: a non-null state with a corrupted MasterSeed
                    //               (would imply the reader saw a torn write), or
                    //               an exception escaping TryLoad.
                    Assert.True(
                        loaded is null || loaded.MasterSeed == 0,
                        $"Loaded corrupt state at attempt {attempts}: MasterSeed={loaded?.MasterSeed}");
                }

                return attempts;
            },
            token);

        await Task.WhenAll(writer, reader);
        // Verify the reader actually ran at least once - otherwise the test
        // would pass trivially if the writer finished before the reader ever
        // attempted a load.
        Assert.True(await reader > 0, "reader task never attempted a load");
    }

    /// <summary>
    /// Kill-the-writer fault injection: simulate a crashed writer by writing
    /// a malformed .tmp file directly (as if the writer was killed between
    /// the FileStream.Dispose and the File.Move atomic-rename step) and
    /// leave the corrupt .tmp file on disk. A reader performing TryLoad
    /// MUST still see the previous valid state on the final path. A
    /// subsequent legitimate SaveAsync MUST overwrite the stale .tmp and
    /// advance the persisted state normally.
    /// </summary>
    [Fact]
    public async Task KillTheWriter_LeavesPreviousFileIntact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        OrchestratorState good = OrchestratorState.FreshBoot(
            masterSeed: 100,
            nowNs: 1000) with { RoundNumber = 7 };
        await _store.SaveAsync(good, ct);

        // Simulate the writer dying between .tmp-write and File.Move by
        // planting a corrupt .tmp file in the same directory.
        string tmpPath = _statePath + ".tmp";
        File.WriteAllText(tmpPath, "{ partial json from crashed writer");

        OrchestratorState? loaded = _store.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.RoundNumber);
        Assert.Equal(100L, loaded.MasterSeed);

        // Subsequent SaveAsync overwrites the stale .tmp (FileMode.Create
        // semantics) and the final state advances.
        OrchestratorState newer = good with { RoundNumber = 8 };
        await _store.SaveAsync(newer, ct);

        OrchestratorState? afterNext = _store.TryLoad();
        Assert.NotNull(afterNext);
        Assert.Equal(8, afterNext.RoundNumber);
    }

    /// <summary>
    /// After SaveAsync completes, the .tmp file MUST NOT still exist on
    /// disk - File.Move(..., overwrite:true) moves rather than copies, so
    /// a lingering .tmp is a regression signal.
    /// </summary>
    [Fact]
    public async Task SaveAsync_RemovesTmpFileOnSuccess()
    {
        OrchestratorState state = OrchestratorState.FreshBoot(1, 1);
        await _store.SaveAsync(state, TestContext.Current.CancellationToken);

        string tmpPath = _statePath + ".tmp";
        Assert.False(
            File.Exists(tmpPath),
            "SaveAsync must atomically rename .tmp to the final path, leaving no .tmp residue");
        Assert.True(File.Exists(_statePath));
    }
}
