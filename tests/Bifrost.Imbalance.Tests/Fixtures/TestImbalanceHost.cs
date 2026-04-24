using System.Threading.Channels;
using Bifrost.Exchange.Application.RoundState;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bifrost.Imbalance.Tests.Fixtures;

/// <summary>
/// Composition-root helper for the simulator integration tests. Wires the
/// <see cref="SimulatorActorLoop"/> against a <see cref="FakeTimeProvider"/>-
/// backed clock, a <see cref="MockRoundStateSource"/> that the test can flip
/// mid-run, a <see cref="CapturingEventPublisher"/> substituted for the real
/// <c>BufferedEventPublisher</c>, and a shared bounded
/// <see cref="Channel{T}"/> of <see cref="SimulatorMessage"/>.
/// <para>
/// <see cref="AdvanceSecondsAsync"/> steps the FakeTimeProvider in 500 ms
/// increments, yielding between steps so any await-continuation wired onto
/// the clock (forecast timer, future PeriodicTimer-driven loops) gets a
/// chance to execute before the next tick fires. Mirrors
/// <c>TestQuoterHost.AdvanceSecondsAsync</c>.
/// </para>
/// </summary>
public sealed class TestImbalanceHost : IAsyncDisposable
{
    public FakeTimeProvider Time { get; }
    public FakeClock Clock { get; }
    public MockRoundStateSource RoundStateSource { get; }
    public CapturingEventPublisher Publisher { get; }
    public SimulatorState State { get; }
    public SeededRandomSource Rng { get; }
    public ImbalancePricingEngine Pricing { get; }
    public Channel<SimulatorMessage> Channel { get; }
    public ImbalanceSimulatorOptions Options { get; }
    public SimulatorActorLoop ActorLoop { get; }
    public QuarterIndexResolver QuarterResolver { get; }

    private readonly Task _loopTask;
    private readonly CancellationTokenSource _cts = new();

    public TestImbalanceHost(
        RoundState initialState = RoundState.IterationOpen,
        ImbalanceSimulatorOptions? options = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));
        Clock = new FakeClock(Time);
        RoundStateSource = new MockRoundStateSource(Clock, initialState);
        Publisher = new CapturingEventPublisher();
        Options = options ?? new ImbalanceSimulatorOptions();
        State = new SimulatorState { CurrentRoundState = initialState };
        Rng = new SeededRandomSource(Options.ScenarioSeed);
        Pricing = new ImbalancePricingEngine(Microsoft.Extensions.Options.Options.Create(Options));
        QuarterResolver = new QuarterIndexResolver();
        Channel = System.Threading.Channels.Channel.CreateBounded<SimulatorMessage>(
            new BoundedChannelOptions(8192)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        ActorLoop = new SimulatorActorLoop(
            Channel,
            State,
            Rng,
            Microsoft.Extensions.Options.Options.Create(Options),
            Clock,
            Publisher,
            Pricing,
            NullLogger<SimulatorActorLoop>.Instance);

        _loopTask = ActorLoop.StartAsync(_cts.Token);
    }

    /// <summary>
    /// Advance the FakeTimeProvider by <paramref name="seconds"/> in 500 ms
    /// increments, yielding to the threadpool between steps so any
    /// await-continuation wired onto the clock can execute before the next
    /// Advance fires.
    /// </summary>
    public async Task AdvanceSecondsAsync(double seconds)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(seconds * 2.0));
        for (var i = 0; i < steps; i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1);
        }
        await Task.Delay(1);
    }

    /// <summary>
    /// Enqueue a <see cref="SimulatorMessage"/> onto the shared channel and
    /// yield briefly so the drain loop has a chance to process it before the
    /// caller asserts. For multi-message orderings, callers should await each
    /// InjectAsync individually.
    /// </summary>
    public async Task InjectAsync(SimulatorMessage msg)
    {
        await Channel.Writer.WriteAsync(msg);
        await Task.Delay(1);
    }

    public async ValueTask DisposeAsync()
    {
        Channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _loopTask; }
        catch { /* cancellation is expected */ }
        _cts.Dispose();
    }
}
