using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Imbalance.HostedServices;
using Bifrost.Imbalance.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// IMB-07 cadence invariant tests. Proves:
/// <list type="bullet">
/// <item>Exactly one public forecast per TForecastSeconds tick during RoundOpen.</item>
/// <item>Zero forecasts during IterationOpen / AuctionOpen / AuctionClosed
/// (the producer fires but the drain-loop gate suppresses publication).</item>
/// <item>Over a 10-minute RoundOpen window at cadence 15s the count lands at
/// ~40 publications (±1 for Advance-step boundary artefacts).</item>
/// </list>
/// <para>
/// Test strategy: wire a real <see cref="ForecastTimerHostedService"/> against
/// the same <see cref="FakeTimeProvider"/> and shared
/// <see cref="System.Threading.Channels.Channel{T}"/> the
/// <see cref="TestImbalanceHost"/> exposes, then advance virtual time in
/// <see cref="TestImbalanceHost.AdvanceSecondsAsync"/>. The PeriodicTimer fires
/// on every <c>FakeTimeProvider.Advance</c> boundary; the drain loop's
/// HandleForecastTick arm does the gating + emission.
/// </para>
/// </summary>
public class ForecastCadenceTests
{
    private static ImbalanceSimulatorOptions MakeOptions(
        int tForecastSeconds = 15,
        int roundDurationSeconds = 600)
        => new ImbalanceSimulatorOptions
        {
            TForecastSeconds = tForecastSeconds,
            TTransientSeconds = 30,
            RoundDurationSeconds = roundDurationSeconds,
            SigmaZeroEuroMwh = 20.0,
            SigmaGateEuroMwh = 1.0,
            DefaultRegime = "Calm",
            ScenarioSeed = 42L,
        };

    /// <summary>
    /// Drive RoundOpen, advance exactly one cadence interval, assert one
    /// forecast was published. Advance a second interval, assert exactly two.
    /// The +1 shape isolates cadence-per-tick from any burst behaviour at
    /// RoundOpen entry.
    /// </summary>
    [Fact]
    public async Task Cadence_EmitsExactlyOneForecastPerTick_DuringRoundOpen()
    {
        var options = MakeOptions();
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Transition IterationOpen -> AuctionOpen -> AuctionClosed -> RoundOpen
            // through the actor-loop channel so HandleRoundStateTransition stamps
            // _roundOpenTsNs from the RoundStateMessage.TsNs on the RoundOpen arm.
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
            var roundOpenTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionClosed, RoundState.RoundOpen, roundOpenTs));

            var before = host.Publisher.CapturedPublic.Count;

            // Advance exactly one cadence interval.
            await host.AdvanceSecondsAsync(options.TForecastSeconds);
            Assert.Equal(1, host.Publisher.CapturedPublic.Count - before);

            // Advance another — expect exactly one more.
            await host.AdvanceSecondsAsync(options.TForecastSeconds);
            Assert.Equal(2, host.Publisher.CapturedPublic.Count - before);

            // Sanity: every captured public event is a ForecastUpdate on the
            // public.forecast routing key with the expected DTO type.
            foreach (var captured in host.Publisher.CapturedPublic)
            {
                Assert.Equal(RabbitMqTopology.PublicForecastRoutingKey, captured.RoutingKey);
                Assert.Equal(MessageTypes.ForecastUpdate, captured.MessageType);
                Assert.IsType<ForecastUpdateEvent>(captured.Evt);
            }
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Advance the whole nominal round duration (600s) at the default 15s
    /// cadence and assert the publication count lands in the [39, 41] window.
    /// Tolerates ±1 for FakeTimeProvider step-boundary artefacts in the host's
    /// 500 ms Advance loop.
    /// </summary>
    [Fact]
    public async Task RoundOpen_TenMinutes_EmitsApproximatelyFortyForecasts()
    {
        var options = MakeOptions(tForecastSeconds: 15, roundDurationSeconds: 600);
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
            var roundOpenTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionClosed, RoundState.RoundOpen, roundOpenTs));

            var before = host.Publisher.CapturedPublic.Count;

            // Advance the full nominal round window.
            await host.AdvanceSecondsAsync(options.RoundDurationSeconds);

            var emitted = host.Publisher.CapturedPublic.Count - before;
            Assert.InRange(emitted, 39, 41);
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Stay outside RoundOpen (transition only to AuctionOpen + AuctionClosed)
    /// and advance several cadence intervals. Assert no forecasts were
    /// published — the drain loop's state gate suppresses publication even
    /// though the timer fired ticks onto the channel.
    /// </summary>
    [Fact]
    public async Task OutsideRoundOpen_EmitsZeroForecasts()
    {
        var options = MakeOptions();
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // IterationOpen (initial) — advance two cadence intervals.
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);
            Assert.Empty(host.Publisher.CapturedPublic);

            // AuctionOpen — two more intervals.
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);
            Assert.Empty(host.Publisher.CapturedPublic);

            // AuctionClosed — two more intervals.
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);
            Assert.Empty(host.Publisher.CapturedPublic);
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Prove that forecasts STOP being published immediately when the round
    /// transitions from RoundOpen to Gate — no grace window, no lingering
    /// forecasts past the round boundary. This is the inverse of the
    /// Cadence_EmitsExactlyOneForecastPerTick test: it locks the state gate
    /// on the downstream side of the round lifecycle.
    /// </summary>
    [Fact]
    public async Task TransitionToGate_StopsForecastPublication()
    {
        var options = MakeOptions();
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
            var roundOpenTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionClosed, RoundState.RoundOpen, roundOpenTs));

            // One cadence interval of RoundOpen → one forecast.
            await host.AdvanceSecondsAsync(options.TForecastSeconds);
            var duringRound = host.Publisher.CapturedPublic.Count;
            Assert.Equal(1, duringRound);

            // Transition to Gate.
            var gateTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.RoundOpen, RoundState.Gate, gateTs));

            // Advance several more cadence intervals — count must not grow.
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 3);
            Assert.Equal(duringRound, host.Publisher.CapturedPublic.Count);
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
