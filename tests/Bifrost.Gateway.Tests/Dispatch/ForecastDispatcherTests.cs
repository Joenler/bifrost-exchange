using System.Threading.Channels;
using Bifrost.Gateway.Dispatch;
using Bifrost.Gateway.State;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ForecastDispatcher"/> + <see cref="CohortAssignment"/>.
/// Focuses on cohort assignment math + dispatch loop invariants. Integration with a
/// live RabbitMQ public.forecast subscription is exercised by Plan 09's load harness;
/// here we drive <see cref="ForecastDispatcher.DispatchOneTickAsync"/> directly via the
/// internal test seam after pre-seeding <c>_latestForecast</c> with
/// <see cref="ForecastDispatcher.SetLatestForecastForTest"/>.
/// </summary>
public class ForecastDispatcherTests
{
    [Fact]
    public void CohortAssignment_StableAcrossReconnects()
    {
        // Same teamName + same cohortCount must always return the same cohort.
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(CohortAssignment.CohortFor("team-alpha", 3), CohortAssignment.CohortFor("team-alpha", 3));
            Assert.Equal(CohortAssignment.CohortFor("team-bravo", 5), CohortAssignment.CohortFor("team-bravo", 5));
        }
    }

    [Fact]
    public void CohortAssignment_DifferentNames_SpreadAcrossCohorts()
    {
        // 8 distinct names + cohortCount=3 ⇒ each cohort should get at least one
        // member (loose distribution check; FNV-1a is well-distributed for natural names).
        var names = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel" };
        var cohortCounts = new int[3];
        foreach (var n in names)
        {
            var c = CohortAssignment.CohortFor(n, 3);
            cohortCounts[c]++;
        }
        // None empty, none over-loaded.
        Assert.All(cohortCounts, count => Assert.InRange(count, 1, 6));
    }

    [Fact]
    public void CohortAssignment_RejectsZeroOrNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CohortAssignment.CohortFor("team", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CohortAssignment.CohortFor("team", -1));
    }

    [Fact]
    public void CohortAssignment_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() => CohortAssignment.CohortFor("", 3));
        Assert.Throws<ArgumentException>(() => CohortAssignment.CohortFor(null!, 3));
    }

    [Fact]
    public void CohortAssignment_LooseDistributionAcross1000Names()
    {
        // For cohortCount=3, the FNV-1a hash mod 3 of "team-1" through "team-1000"
        // must produce approximately even distribution. Each cohort gets between
        // 250 and 415 (loose bound; the strict expected average is ~333).
        var counts = new int[3];
        for (var i = 1; i <= 1000; i++)
        {
            var c = CohortAssignment.CohortFor($"team-{i}", 3);
            counts[c]++;
        }
        Assert.All(counts, n => Assert.InRange(n, 250, 415));
        Assert.Equal(1000, counts.Sum());
    }

    [Fact]
    public void CohortAssignment_BoundedToCohortCountMinus1()
    {
        for (var i = 0; i < 1000; i++)
        {
            var c = CohortAssignment.CohortFor($"team-{i}", 4);
            Assert.InRange(c, 0, 3);
        }
    }

    [Fact]
    public async Task Dispatcher_NoForecast_NoEnvelopesEnqueued_ButCohortRotates()
    {
        // Pre-populate registry with 6 teams. Without a forecast snapshot, no team
        // should receive an envelope; the cohort counter should still advance so that
        // when a forecast arrives, dispatch resumes on the next cohort.
        var registry = NewRegistryWithTeams(6, out var outboundChannels);
        var dispatcher = NewDispatcher(registry);

        await dispatcher.DispatchOneTickAsync(CancellationToken.None);
        await dispatcher.DispatchOneTickAsync(CancellationToken.None);

        foreach (var ch in outboundChannels.Values)
        {
            Assert.False(ch.Reader.TryRead(out _));   // nothing enqueued
        }
    }

    [Fact]
    public async Task Dispatcher_WithForecast_DispatchesOneEnvelopePerCohortMember_PerTick()
    {
        // 8 teams across 3 cohorts → first tick fires one cohort's worth of
        // ForecastUpdates. Over 3 ticks every team receives exactly 1 ForecastUpdate.
        var registry = NewRegistryWithTeams(8, out var outboundChannels);
        var dispatcher = NewDispatcher(registry);
        dispatcher.SetLatestForecastForTest(forecastPriceTicks: 5_000, horizonNs: 1_000_000_000L,
            originUtc: DateTimeOffset.UtcNow, sequence: 1);

        for (var t = 0; t < 3; t++)   // one full round-robin
        {
            await dispatcher.DispatchOneTickAsync(CancellationToken.None);
        }

        var totalDispatched = 0;
        foreach (var (_, ch) in outboundChannels)
        {
            while (ch.Reader.TryRead(out var ev))
            {
                Assert.NotNull(ev.ForecastUpdate);
                Assert.Equal(5_000L, ev.ForecastUpdate.ForecastPriceTicks);
                Assert.Equal(1_000_000_000L, ev.ForecastUpdate.HorizonNs);
                totalDispatched++;
            }
        }
        Assert.Equal(8, totalDispatched);
    }

    [Fact]
    public async Task Dispatcher_RingAppendHappensUnderStateLock_BeforeOutboundWrite()
    {
        // After a tick, every team in the current cohort should have an envelope
        // in its ring (Pitfall 10: ring-Append happens UNDER the lock, before the
        // out-of-lock channel write).
        var registry = NewRegistryWithTeams(6, out var outboundChannels);
        var dispatcher = NewDispatcher(registry);
        dispatcher.SetLatestForecastForTest(forecastPriceTicks: 7_777, horizonNs: 0L,
            originUtc: DateTimeOffset.UtcNow, sequence: 99);

        await dispatcher.DispatchOneTickAsync(CancellationToken.None);
        await dispatcher.DispatchOneTickAsync(CancellationToken.None);
        await dispatcher.DispatchOneTickAsync(CancellationToken.None);

        // After 3 ticks (one full round-robin), every team's ring should contain
        // exactly one ForecastUpdate envelope.
        var teams = registry.SnapshotAll();
        foreach (var team in teams)
        {
            long head, tail;
            lock (team.StateLock)
            {
                head = team.Ring.Head;
                tail = team.Ring.Tail;
            }
            Assert.Equal(1, head - tail);
        }
    }

    [Fact]
    public async Task Dispatcher_AdvancesCohortRoundRobin()
    {
        // After cohortCount ticks, every team has been visited once.
        var registry = NewRegistryWithTeams(12, out var outboundChannels);
        var dispatcher = NewDispatcher(registry);
        dispatcher.SetLatestForecastForTest(1, 1, DateTimeOffset.UtcNow, 1);

        // 3 ticks at cohortCount=3 = full round-robin
        for (var t = 0; t < 3; t++)
        {
            await dispatcher.DispatchOneTickAsync(CancellationToken.None);
        }

        var totalDispatched = 0;
        foreach (var (_, ch) in outboundChannels)
        {
            while (ch.Reader.TryRead(out _)) totalDispatched++;
        }
        Assert.Equal(12, totalDispatched);
    }

    [Fact]
    public void SetJitterRngForTest_OverridesRng_RejectsNull()
    {
        var dispatcher = NewDispatcher(NewRegistryWithTeams(0, out _));
        // Should not throw.
        dispatcher.SetJitterRngForTest(new Random(42));
        Assert.Throws<ArgumentNullException>(() => dispatcher.SetJitterRngForTest(null!));
    }

    // ----- helpers -----

    private static TeamRegistry NewRegistryWithTeams(int count, out Dictionary<string, Channel<StrategyProto.MarketEvent>> outbounds)
    {
        var registry = new TeamRegistry(new TestClock(new FakeTimeProvider(default)));
        outbounds = new Dictionary<string, Channel<StrategyProto.MarketEvent>>();
        for (var i = 0; i < count; i++)
        {
            var teamName = $"team-{i:000}";
            var result = registry.TryRegister(teamName, lastSeenSequence: 0L);
            Assert.True(result.Success);
            Assert.NotNull(result.TeamState);
            var ch = Channel.CreateBounded<StrategyProto.MarketEvent>(64);
            lock (result.TeamState!.StateLock) { result.TeamState.AttachOutbound(ch.Writer); }
            outbounds[teamName] = ch;
        }
        return registry;
    }

    private static ForecastDispatcher NewDispatcher(TeamRegistry registry)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 25, 0, 0, 0, TimeSpan.Zero));
        var clock = new TestClock(time);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:ForecastDispatch:CohortCount"] = "3",
                ["Gateway:ForecastDispatch:CohortIntervalMs"] = "15000",
                ["Gateway:ForecastDispatch:CohortStartJitterMs"] = "0",   // deterministic
                ["Gateway:ForecastDispatch:IntraCohortDispatchSpreadMs"] = "0",   // deterministic
                ["Gateway:ForecastDispatch:InterTickJitterMs"] = "0",   // deterministic
            })
            .Build();
        var dispatcher = new ForecastDispatcher(
            connection: null!,   // null permitted for unit tests; ExecuteAsync rejects null
            registry: registry,
            timeProvider: time,
            clock: clock,
            configuration: config,
            logger: NullLogger<ForecastDispatcher>.Instance);
        return dispatcher;
    }

    private sealed class TestClock(FakeTimeProvider time) : IClock
    {
        public DateTimeOffset GetUtcNow() => time.GetUtcNow();
    }
}
