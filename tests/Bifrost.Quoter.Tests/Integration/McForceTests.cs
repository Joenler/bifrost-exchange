using Bifrost.Quoter.Schedule;
using Bifrost.Quoter.Tests.Fixtures;
using Xunit;

namespace Bifrost.Quoter.Tests.Integration;

/// <summary>
/// QTR-04 integration tests. MC-force install + persistence + beat-boundary
/// release + nonce idempotency, all observed end-to-end through the Quoter
/// loop driven by FakeTimeProvider, with the captured TestRabbitPublisher
/// stream serving as ground truth for what the operator-facing world sees.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class McForceTests
{
    [Fact]
    public async Task McForce_InstallsNewRegime_WithMcForcedTrueFlag()
    {
        // calm-drift starts in CALM. Inject an MC force to VOLATILE; the
        // very next Quoter tick must consume the inbox, install the force,
        // and emit a RegimeChange envelope with mcForced=true.
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost("calm-drift.json", overrideSeed: 100);
        await host.Quoter.StartAsync(ct);
        // Allow the scheduler to settle (a tick or two with no transitions).
        await host.AdvanceSecondsAsync(1.0);

        var nonce = Guid.NewGuid();
        await host.Inbox.Writer.WriteAsync(new RegimeForceMessage(Regime.Volatile, nonce), ct);

        await host.AdvanceSecondsAsync(1.0);
        await host.Quoter.StopAsync(ct);

        var regimeChanges = host.TestPublisher.Captured
            .Where(c => c.Kind == "RegimeChange")
            .ToList();
        Assert.NotEmpty(regimeChanges);
        var forceEvent = regimeChanges.FirstOrDefault(c =>
            c.JsonBody.Contains("\"mcForced\":true", StringComparison.Ordinal));
        Assert.NotEqual(default, forceEvent);
        Assert.Contains("\"to\":\"Volatile\"", forceEvent.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McForce_PersistsUntilNextBeatBoundary()
    {
        // regime-sweep has 60-second beats. Install MC force during beat 1
        // (CALM, t=0..60). Within the beat, additional ticks must NOT
        // produce additional RegimeChange events (the schedule is suppressed
        // by the active force).
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost("regime-sweep.json", overrideSeed: 4242);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(1.0);

        var nonce = Guid.NewGuid();
        await host.Inbox.Writer.WriteAsync(new RegimeForceMessage(Regime.Shock, nonce), ct);

        // Drain the inbox + install force + emit one MC RegimeChange.
        await host.AdvanceSecondsAsync(1.0);

        var afterInstallCount = host.TestPublisher.Captured
            .Count(c => c.Kind == "RegimeChange");
        Assert.True(afterInstallCount >= 1, $"expected at least one RegimeChange after install, got {afterInstallCount}");

        // Advance another 30 simulated seconds within the same beat. No new
        // RegimeChange events should fire because Markov is suppressed.
        await host.AdvanceSecondsAsync(30.0);
        var afterPersistCount = host.TestPublisher.Captured
            .Count(c => c.Kind == "RegimeChange");
        Assert.Equal(afterInstallCount, afterPersistCount);

        await host.Quoter.StopAsync(ct);
    }

    [Fact]
    public async Task McForce_ClearedAtBeatBoundary()
    {
        // Same fixture: beat 1 = CALM 0..60, beat 2 = TRENDING 60..120.
        // Install an MC force to SHOCK during beat 1. Advance past the
        // boundary at t=60. The boundary crossing must produce a
        // RegimeChange with mcForced=false and reason=BeatBoundary, restoring
        // the scheduled beat regime (TRENDING).
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost("regime-sweep.json", overrideSeed: 4242);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(1.0);

        var nonce = Guid.NewGuid();
        await host.Inbox.Writer.WriteAsync(new RegimeForceMessage(Regime.Shock, nonce), ct);
        await host.AdvanceSecondsAsync(1.0);

        // Advance past the beat boundary (t=60). 65s is enough to be safely
        // across without depending on tick alignment.
        await host.AdvanceSecondsAsync(65.0);
        await host.Quoter.StopAsync(ct);

        var regimeChanges = host.TestPublisher.Captured
            .Where(c => c.Kind == "RegimeChange")
            .ToList();
        // Boundary-crossing emission: from Shock (held by force) to Trending
        // (scheduled beat 2 regime), mcForced=false.
        var boundaryEvent = regimeChanges.FirstOrDefault(c =>
            c.JsonBody.Contains("\"reason\":\"BeatBoundary\"", StringComparison.Ordinal));
        Assert.NotEqual(default, boundaryEvent);
        Assert.Contains("\"mcForced\":false", boundaryEvent.JsonBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McForce_DuplicateNonce_ProducesNoAdditionalEmission()
    {
        // Install MC force with nonce X. Then write a SECOND message with
        // the same nonce but a different requested regime. The schedule's
        // LRU dedup must reject the replay; no additional RegimeChange
        // emission appears in the captured stream.
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost("calm-drift.json", overrideSeed: 100);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(1.0);

        var nonce = Guid.NewGuid();
        await host.Inbox.Writer.WriteAsync(new RegimeForceMessage(Regime.Volatile, nonce), ct);
        await host.AdvanceSecondsAsync(1.0);

        var afterFirst = host.TestPublisher.Captured
            .Count(c => c.Kind == "RegimeChange");
        Assert.True(afterFirst >= 1);

        // Replay with same nonce -- should be a no-op at the LRU dedup gate.
        await host.Inbox.Writer.WriteAsync(new RegimeForceMessage(Regime.Shock, nonce), ct);
        await host.AdvanceSecondsAsync(1.0);

        var afterReplay = host.TestPublisher.Captured
            .Count(c => c.Kind == "RegimeChange");
        Assert.Equal(afterFirst, afterReplay);

        await host.Quoter.StopAsync(ct);
    }
}
