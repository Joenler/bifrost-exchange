using System.Text.Json;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.News;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Orchestrator.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Orchestrator.Tests.Rabbit;

/// <summary>
/// Drives the orchestrator actor end-to-end (single-writer drain loop +
/// in-memory RabbitMQ capturing channel + real FileSystemNewsLibrary on a
/// fixture JSON) for every event-emitting MC command variant. Each fact
/// boots its own actor + temp state file + capturing harness so the tests
/// run in parallel without state leakage.
/// </summary>
/// <remarks>
/// The fixture JSON ships in TestFixtures/news-library-test.json (CopyToOutput
/// so AppContext.BaseDirectory resolves it under bin/). It carries two
/// entries: <c>thor-strike</c> (with shock — exercises the events.news +
/// events.physical_shock dual-publish path) and <c>freyr-favor</c> (no shock
/// — covers the optional-shock branch in case future facts need it).
/// </remarks>
public sealed class EventEmissionTests : IAsyncDisposable
{
    private readonly string _tempDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"bifrost-orch-events-{Guid.NewGuid():N}")).FullName;

    private readonly string _fixturePath;

    public EventEmissionTests()
    {
        _fixturePath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "news-library-test.json");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort temp-dir cleanup
        }

        await ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NewsFire_ThorStrike_EmitsNewsAndPhysicalShockEnvelopes()
    {
        ActorRig rig = await BuildAndStartActorAsync();

        rig.Harness.Clear();

        TaskCompletionSource<McCommandResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: 1_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    NewsFire = new NewsFireCmd { LibraryKey = "thor-strike" },
                },
                Tcs: tcs,
                SourceTag: "test"),
            rig.Cts.Token);

        McCommandResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), rig.Cts.Token);
        Assert.True(result.Success, $"Expected success, got: {result.Message}");

        // Drain any in-flight publishes that the actor enqueued before the TCS
        // completed. The capturing channel finishes synchronously inside the
        // BasicPublishAsync call so a small wait is sufficient even under
        // CI load.
        await Task.Delay(100, rig.Cts.Token);

        IReadOnlyList<CapturedPublish> news =
            rig.Harness.CapturedWithRoutingPrefix(OrchestratorRabbitMqTopology.EventsNewsRoutingKey);
        IReadOnlyList<CapturedPublish> shocks =
            rig.Harness.CapturedWithRoutingPrefix(OrchestratorRabbitMqTopology.EventsPhysicalShockRoutingKey);

        Assert.Single(news);
        Assert.Single(shocks);

        Envelope<NewsPayload>? newsEnv = JsonSerializer.Deserialize<Envelope<NewsPayload>>(
            news[0].BodyAsString, CamelCaseOptions);
        Assert.NotNull(newsEnv);
        Assert.Equal(MessageTypes.News, newsEnv!.MessageType);
        Assert.Contains("Thor", newsEnv.Payload.Text);
        Assert.Equal("thor-strike", newsEnv.Payload.LibraryKey);
        Assert.Equal("urgent", newsEnv.Payload.Severity);

        Envelope<PhysicalShockPayload>? shockEnv = JsonSerializer.Deserialize<Envelope<PhysicalShockPayload>>(
            shocks[0].BodyAsString, CamelCaseOptions);
        Assert.NotNull(shockEnv);
        Assert.Equal(MessageTypes.PhysicalShock, shockEnv!.MessageType);
        Assert.Equal(-300, shockEnv.Payload.Mw);
        Assert.Equal("midgard-station-trip", shockEnv.Payload.Label);
        Assert.Equal("round", shockEnv.Payload.Persistence);
        Assert.Null(shockEnv.Payload.QuarterIndex);

        await rig.StopAsync();
    }

    [Fact]
    public async Task NewsFire_UnknownKey_RejectsWithTypedMessage_AndPublishesZeroEvents()
    {
        ActorRig rig = await BuildAndStartActorAsync();

        rig.Harness.Clear();

        TaskCompletionSource<McCommandResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: 1_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    NewsFire = new NewsFireCmd { LibraryKey = "nonexistent-key" },
                },
                Tcs: tcs,
                SourceTag: "test"),
            rig.Cts.Token);

        McCommandResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), rig.Cts.Token);

        Assert.False(result.Success);
        Assert.Contains("unknown library key", result.Message);
        Assert.Contains("nonexistent-key", result.Message);

        // Library miss MUST publish zero events.* envelopes. The audit log
        // publish on bifrost.mc.v1 is a separate channel — only assert here on
        // the public events stream.
        await Task.Delay(100, rig.Cts.Token);
        Assert.DoesNotContain(rig.Harness.Captured, c =>
            c.RoutingKey.StartsWith("events.", StringComparison.Ordinal));

        await rig.StopAsync();
    }

    [Fact]
    public async Task NewsPublish_FreeText_EmitsOneNewsEnvelopeWithEmptyLibraryKey()
    {
        ActorRig rig = await BuildAndStartActorAsync();

        rig.Harness.Clear();

        TaskCompletionSource<McCommandResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: 1_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    NewsPublish = new NewsPublishCmd { Text = "Event starting soon." },
                },
                Tcs: tcs,
                SourceTag: "test"),
            rig.Cts.Token);

        McCommandResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), rig.Cts.Token);
        Assert.True(result.Success, $"Expected success, got: {result.Message}");

        await Task.Delay(100, rig.Cts.Token);

        IReadOnlyList<CapturedPublish> news =
            rig.Harness.CapturedWithRoutingPrefix(OrchestratorRabbitMqTopology.EventsNewsRoutingKey);
        Assert.Single(news);

        Envelope<NewsPayload>? env = JsonSerializer.Deserialize<Envelope<NewsPayload>>(
            news[0].BodyAsString, CamelCaseOptions);
        Assert.NotNull(env);
        Assert.Equal(string.Empty, env!.Payload.LibraryKey);
        Assert.Equal("Event starting soon.", env.Payload.Text);
        Assert.Equal("info", env.Payload.Severity);

        // No shock envelope; no other events.* publishes either.
        Assert.Empty(rig.Harness.CapturedWithRoutingPrefix(
            OrchestratorRabbitMqTopology.EventsPhysicalShockRoutingKey));

        await rig.StopAsync();
    }

    [Fact]
    public async Task AlertUrgent_EmitsMarketAlertEnvelopeWithUrgentSeverity()
    {
        ActorRig rig = await BuildAndStartActorAsync();

        rig.Harness.Clear();

        TaskCompletionSource<McCommandResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: 1_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    AlertUrgent = new AlertUrgentCmd { Text = "Trading halted: line tripped." },
                },
                Tcs: tcs,
                SourceTag: "test"),
            rig.Cts.Token);

        McCommandResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), rig.Cts.Token);
        Assert.True(result.Success, $"Expected success, got: {result.Message}");

        await Task.Delay(100, rig.Cts.Token);

        IReadOnlyList<CapturedPublish> alerts =
            rig.Harness.CapturedWithRoutingPrefix(OrchestratorRabbitMqTopology.EventsMarketAlertRoutingKey);
        Assert.Single(alerts);

        Envelope<MarketAlertPayload>? env = JsonSerializer.Deserialize<Envelope<MarketAlertPayload>>(
            alerts[0].BodyAsString, CamelCaseOptions);
        Assert.NotNull(env);
        Assert.Equal(MessageTypes.MarketAlert, env!.MessageType);
        Assert.Equal("Trading halted: line tripped.", env.Payload.Text);
        Assert.Equal("urgent", env.Payload.Severity);

        await rig.StopAsync();
    }

    [Fact]
    public async Task ConfigSet_EmitsConfigChangeEnvelopeWithProvidedPathAndNewValue()
    {
        ActorRig rig = await BuildAndStartActorAsync();

        rig.Harness.Clear();

        TaskCompletionSource<McCommandResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: 1_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    ConfigSet = new ConfigSetCmd { Path = "scoring.weight", Value = "0.42" },
                },
                Tcs: tcs,
                SourceTag: "test"),
            rig.Cts.Token);

        McCommandResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), rig.Cts.Token);
        Assert.True(result.Success, $"Expected success, got: {result.Message}");

        await Task.Delay(100, rig.Cts.Token);

        IReadOnlyList<CapturedPublish> changes =
            rig.Harness.CapturedWithRoutingPrefix(OrchestratorRabbitMqTopology.EventsConfigChangeRoutingKey);
        Assert.Single(changes);

        Envelope<ConfigChangePayload>? env = JsonSerializer.Deserialize<Envelope<ConfigChangePayload>>(
            changes[0].BodyAsString, CamelCaseOptions);
        Assert.NotNull(env);
        Assert.Equal(MessageTypes.ConfigChange, env!.MessageType);
        Assert.Equal("scoring.weight", env.Payload.Path);
        Assert.Equal("0.42", env.Payload.NewValue);
        Assert.Equal(string.Empty, env.Payload.OldValue);

        await rig.StopAsync();
    }

    [Fact]
    public async Task RegimeForce_Volatile_PublishesToBifrostMcExchange_NotEventsRegimeChange()
    {
        // D-14 amendment gate: orchestrator MUST route RegimeForce to
        // bifrost.mc / mc.regime.force so the quoter consumes and emits the
        // public Event.RegimeChange envelope. The orchestrator MUST NOT emit
        // events.regime_change directly — Phase 03 D-17 locks the quoter as
        // the sole emitter.
        ActorRig rig = await BuildAndStartActorAsync();

        rig.Harness.Clear();

        TaskCompletionSource<McCommandResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Channel.Writer.WriteAsync(
            new McCommandMessage(
                TsNs: 1_000L,
                Cmd: new McCommand
                {
                    OperatorHost = "mc",
                    Confirm = true,
                    RegimeForce = new RegimeForceCmd
                    {
                        Regime = Bifrost.Contracts.Events.Regime.Volatile,
                    },
                },
                Tcs: tcs,
                SourceTag: "test"),
            rig.Cts.Token);

        McCommandResult result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), rig.Cts.Token);
        Assert.True(result.Success, $"Expected success, got: {result.Message}");

        await Task.Delay(100, rig.Cts.Token);

        // Exactly one publish on bifrost.mc / mc.regime.force.
        IReadOnlyList<CapturedPublish> regimeForcePublishes = rig.Harness.Captured
            .Where(c => c.Exchange == OrchestratorRabbitMqTopology.QuoterMcExchange
                     && c.RoutingKey == OrchestratorRabbitMqTopology.QuoterMcRegimeRoutingKey)
            .ToList();
        Assert.Single(regimeForcePublishes);

        // Literal-string sanity check: no environment-config-driven divergence
        // from D-14's "bifrost.mc" + "mc.regime.force" wire requirement.
        Assert.Equal("bifrost.mc", regimeForcePublishes[0].Exchange);
        Assert.Equal("mc.regime.force", regimeForcePublishes[0].RoutingKey);

        // Zero publishes on any events.regime* routing key — orchestrator
        // never emits events.regime_change directly.
        Assert.DoesNotContain(rig.Harness.Captured, c =>
            c.RoutingKey.StartsWith("events.regime", StringComparison.Ordinal));

        // Payload shape matches the quoter's McRegimeForceDto wire contract:
        // camelCase "regime" field carrying the PascalCase string name and a
        // non-empty "nonce" field carrying a JSON-serialised Guid.
        using JsonDocument doc = JsonDocument.Parse(regimeForcePublishes[0].BodyAsString);
        JsonElement payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("Volatile", payload.GetProperty("regime").GetString());
        string? nonce = payload.GetProperty("nonce").GetString();
        Assert.False(string.IsNullOrWhiteSpace(nonce));
        Assert.True(Guid.TryParse(nonce, out _),
            $"nonce must parse as a Guid; got '{nonce}'");

        await rig.StopAsync();
    }

    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Wires up the full actor + capturing harness rig used by every fact
    /// in this file. Boots the actor, waits for the reconciliation publish
    /// to clear, then returns the rig with a freshly-cleared harness so the
    /// caller's first publish is the only entry the assertion sees.
    /// </summary>
    private async Task<ActorRig> BuildAndStartActorAsync()
    {
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        IOptions<OrchestratorOptions> opts = Options.Create(new OrchestratorOptions
        {
            StatePath = Path.Combine(_tempDir, $"state-{Guid.NewGuid():N}.json"),
            NewsLibraryPath = _fixturePath,
        });
        RoundStateMachine machine = new();
        JsonStateStore store = new(opts, NullLogger<JsonStateStore>.Instance);
        OrchestratorRabbitMqTopology topology = new(harness.Channel);
        OrchestratorPublisher publisher = new(harness.Channel, clock);
        RoundStateRingBuffer ring = new();
        RoundSeedAllocator seedAllocator = new(masterSeed: 42);
        FileSystemNewsLibrary library = new(opts);

        Channel<IOrchestratorMessage> channel = Channel.CreateBounded<IOrchestratorMessage>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        OrchestratorActor actor = new(
            channel.Reader,
            machine,
            store,
            publisher,
            topology,
            clock,
            opts,
            ring,
            seedAllocator,
            library,
            NullLogger<OrchestratorActor>.Instance);

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        await actor.StartAsync(cts.Token);

        // Reconciliation publish on boot: wait for it to land before the test
        // body's harness.Clear() so the post-boot snapshot does not bleed into
        // the assertion.
        await Task.Delay(150, cts.Token);

        return new ActorRig(actor, channel, harness, cts);
    }

    private sealed record ActorRig(
        OrchestratorActor Actor,
        Channel<IOrchestratorMessage> Channel,
        RabbitMqSubscriberHarness Harness,
        CancellationTokenSource Cts)
    {
        public async Task StopAsync()
        {
            Channel.Writer.Complete();
            try
            {
                await Actor.StopAsync(Cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Test cancellation token already fired — drain-loop shutdown
                // is best-effort.
            }
            finally
            {
                Cts.Dispose();
            }
        }
    }
}
