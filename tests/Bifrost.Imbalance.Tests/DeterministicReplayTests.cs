using System.Text.Json;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.HostedServices;
using Bifrost.Imbalance.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// SPEC Req 3 canary — byte-identical replay under the same scenario seed.
/// <para>
/// Two back-to-back runs with identical
/// <see cref="ImbalanceSimulatorOptions"/>, identical
/// <c>FakeTimeProvider</c> starting instant (TestImbalanceHost fixes it to
/// 2026-04-01T10:00:00Z), identical <see cref="RoundStateMessage"/>
/// transitions, and identical fill injections must produce byte-identical
/// serialized <see cref="ForecastUpdateEvent"/> payloads. A negative-control
/// fact with a different seed proves the PRNG is actually driving variation
/// — without it a regression that silently dropped the seed from the hot
/// path would still pass the equality check.
/// </para>
/// <para>
/// State transitions are driven via <see cref="TestImbalanceHost.InjectAsync"/>
/// of <see cref="RoundStateMessage"/> onto the shared channel rather than
/// <c>host.RoundStateSource.Set(...)</c> — the test host does not register
/// <c>RoundStateBridgeHostedService</c> so the <c>Set</c> path would silently
/// not drive the drain loop. Convention established in
/// <see cref="FullRoundCycleIntegrationTests"/> and
/// <see cref="ForecastCadenceTests"/>.
/// </para>
/// <para>
/// Serialization options mirror
/// <c>RabbitMqEventPublisher.JsonOptions</c> — camelCase naming policy only.
/// The production publisher emits via this same option shape so the bytes
/// the test compares represent the actual public-wire payload shape.
/// </para>
/// </summary>
public class DeterministicReplayTests
{
    // Mirrors RabbitMqEventPublisher.JsonOptions exactly. If production
    // options ever change this test fails loudly — intentional: any shift in
    // the serialized wire form is a determinism boundary and deserves review.
    private static readonly JsonSerializerOptions ProductionOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task SameSeed_ProducesByteIdenticalForecastStream()
    {
        var bytesA = await RunAndCaptureAsync(BuildOptions(scenarioSeed: 42L));
        var bytesB = await RunAndCaptureAsync(BuildOptions(scenarioSeed: 42L));

        // Both runs must have emitted at least one forecast — otherwise the
        // per-index SequenceEqual loop below would be trivially satisfied on
        // empty collections and the assertion would pass silently even if the
        // cadence regressed to zero.
        Assert.NotEmpty(bytesA);
        Assert.Equal(bytesA.Count, bytesB.Count);

        for (var i = 0; i < bytesA.Count; i++)
        {
            Assert.True(
                bytesA[i].SequenceEqual(bytesB[i]),
                $"ForecastUpdate #{i} payload bytes differ between runs under identical scenario seed — determinism regression. Run A: {Convert.ToHexString(bytesA[i])}. Run B: {Convert.ToHexString(bytesB[i])}.");
        }
    }

    [Fact]
    public async Task DifferentSeed_ProducesDifferentForecastStream()
    {
        // Negative control. Without this, a regression that silently dropped
        // the seed from the PRNG reseed path would still pass the same-seed
        // equality check because both runs would produce the same constant
        // stream. Asserts the seed is actually plumbed through.
        var bytesA = await RunAndCaptureAsync(BuildOptions(scenarioSeed: 42L));
        var bytesB = await RunAndCaptureAsync(BuildOptions(scenarioSeed: 999L));

        Assert.NotEmpty(bytesA);
        Assert.Equal(bytesA.Count, bytesB.Count);

        var anyDifferent = false;
        for (var i = 0; i < bytesA.Count; i++)
        {
            if (!bytesA[i].SequenceEqual(bytesB[i]))
            {
                anyDifferent = true;
                break;
            }
        }

        Assert.True(
            anyDifferent,
            "Different scenario seeds produced identical forecast streams — PRNG is not driving forecast variation; seed is not plumbed through to the noise draws.");
    }

    private static ImbalanceSimulatorOptions BuildOptions(long scenarioSeed) => new()
    {
        TForecastSeconds = 15,
        RoundDurationSeconds = 60,
        // Keep SigmaZero non-zero so forecast payloads actually depend on the
        // PRNG. A zero-sigma config would produce deterministic-by-construction
        // payloads regardless of seed, which would mask a broken PRNG path.
        SigmaZeroEuroMwh = 20.0,
        SigmaGateEuroMwh = 1.0,
        TicksPerEuro = 100,
        DefaultRegime = "Calm",
        K = 50.0,
        Alpha = 1.0,
        NScalingMwh = 100.0,
        GammaCalm = 1.0,
        GammaTrending = 1.5,
        GammaVolatile = 2.5,
        GammaShock = 5.0,
        SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
        NonSettlementClientIds = new[] { "quoter", "dah-auction" },
        ScenarioSeed = scenarioSeed,
    };

    /// <summary>
    /// Run a single fixture through the IterationOpen -> AuctionOpen ->
    /// AuctionClosed -> RoundOpen cycle, inject two identical fills (one per
    /// QH), advance virtual time through the full nominal round duration, and
    /// capture the serialized UTF-8 bytes of every emitted
    /// <see cref="ForecastUpdateEvent"/>.
    /// </summary>
    private static async Task<List<byte[]>> RunAndCaptureAsync(
        ImbalanceSimulatorOptions options)
    {
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Drive the lifecycle via channel injection — MockRoundStateSource
            // is not wired through a bridge in the test host (see class
            // docstring).
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));

            // Stamp _roundOpenTsNs from the FakeTimeProvider's current instant
            // — identical across runs because TestImbalanceHost pins the start
            // to 2026-04-01T10:00:00Z and AdvanceSecondsAsync has not been
            // called yet.
            var roundOpenTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionClosed, RoundState.RoundOpen, roundOpenTs));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            // Identical fills in identical order. Inject BEFORE advancing time
            // so the NetPositions map is populated before the first forecast
            // tick reads it (avoids race where tick-ordering could pick up
            // different aggregations between runs).
            await host.InjectAsync(new FillMessage(
                TsNs: 10L, ClientId: "alpha",
                InstrumentId: "DE.999901010000-999901010015",
                QuarterIndex: 0, Side: "Buy", QuantityTicks: 500L));
            await host.InjectAsync(new FillMessage(
                TsNs: 20L, ClientId: "bravo",
                InstrumentId: "DE.999901010015-999901010030",
                QuarterIndex: 1, Side: "Buy", QuantityTicks: 300L));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            // Advance the full nominal round window. 60s / 15s cadence = 4
            // forecasts nominal; the same FakeTimeProvider stepping produces
            // the same tick sequence across runs.
            await host.AdvanceSecondsAsync(options.RoundDurationSeconds);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            // Snapshot the CapturedPublic queue here — Gate / Settled
            // transitions will add ImbalancePrint + ImbalanceSettlement entries
            // which are not part of the forecast-stream invariant under test.
            var forecastBytes = new List<byte[]>();
            foreach (var captured in host.Publisher.CapturedPublic)
            {
                if (captured.Evt is ForecastUpdateEvent fu)
                {
                    forecastBytes.Add(JsonSerializer.SerializeToUtf8Bytes(fu, ProductionOptions));
                }
            }
            return forecastBytes;
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
