using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.Tests.TestSupport;
using Xunit;

namespace Bifrost.Orchestrator.Tests.Rabbit;

/// <summary>
/// ORC-04 integration-style proof: driving the publisher through a full
/// six-state cycle produces exactly six envelopes on
/// <see cref="OrchestratorRabbitMqTopology.RoundExchange"/> with the
/// expected <c>round.state.{snake}</c> routing keys in order and
/// monotonically increasing <c>Envelope.Sequence</c>. Runs entirely on the
/// <see cref="RabbitMqSubscriberHarness"/> in-memory capturing channel —
/// no live RabbitMQ broker required.
/// </summary>
public sealed class RoundStateChangedPublishTests
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task SixStateCycle_PublishesSixEnvelopes_InOrder_WithMonotonicSequence()
    {
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        OrchestratorPublisher publisher = new(harness.Channel, clock);

        // The six scored-round transitions per SPEC Req 1: after Settled the
        // orchestrator accepts NextRound to return to IterationOpen — that's
        // the cycle's closing state and also the first state of the next
        // round. Asserting six publishes (not seven) matches SPEC Req 8's
        // acceptance gate on the lifecycle sequence.
        (string PayloadStateName, string ExpectedRoutingKey)[] cycle =
        {
            ("AuctionOpen", "round.state.auction_open"),
            ("AuctionClosed", "round.state.auction_closed"),
            ("RoundOpen", "round.state.round_open"),
            ("Gate", "round.state.gate"),
            ("Settled", "round.state.settled"),
            ("IterationOpen", "round.state.iteration_open"),
        };

        long sequence = 0;
        foreach ((string stateName, _) in cycle)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            RoundStateChangedPayload payload = new(
                State: stateName,
                RoundNumber: 1,
                ScenarioSeedOnWire: 0,
                TransitionNs: clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L,
                ExpectedNextTransitionNs: null,
                Paused: false,
                PausedReason: null,
                Blocked: false,
                BlockedReason: null,
                IsReconciliation: false,
                IterationSeedRotationCount: 0,
                AbortReason: null);

            await publisher.PublishRoundStateChangedAsync(
                payload,
                ++sequence,
                TestContext.Current.CancellationToken);
        }

        IReadOnlyList<CapturedPublish> captured = harness.CapturedWithRoutingPrefix("round.state.");
        Assert.Equal(6, captured.Count);

        long previousSequence = 0;
        for (int i = 0; i < 6; i++)
        {
            CapturedPublish publish = captured[i];
            Assert.Equal(OrchestratorRabbitMqTopology.RoundExchange, publish.Exchange);
            Assert.Equal(cycle[i].ExpectedRoutingKey, publish.RoutingKey);
            Assert.False(publish.Mandatory);

            Envelope<RoundStateChangedPayload>? envelope =
                JsonSerializer.Deserialize<Envelope<RoundStateChangedPayload>>(
                    publish.BodyAsString,
                    CamelCaseOptions);

            Assert.NotNull(envelope);
            Assert.Equal(MessageTypes.RoundStateChanged, envelope!.MessageType);
            Assert.Equal(i + 1, envelope.Sequence);
            Assert.Equal(cycle[i].PayloadStateName, envelope.Payload.State);
            Assert.True(envelope.Sequence > previousSequence,
                $"Sequence must be strictly monotonic: got {envelope.Sequence} after {previousSequence}");
            previousSequence = envelope.Sequence ?? throw new InvalidOperationException(
                "Envelope.Sequence must be non-null on RoundStateChanged publishes");
        }
    }

    [Fact]
    public async Task AbortedState_RoutesToSnakeCasedAbortedKey()
    {
        RabbitMqSubscriberHarness harness = new();
        OrchestratorPublisher publisher = new(harness.Channel, new FakeClock());

        RoundStateChangedPayload payload = new(
            State: "Aborted",
            RoundNumber: 2,
            ScenarioSeedOnWire: 0,
            TransitionNs: 1714516800000000000L,
            ExpectedNextTransitionNs: null,
            Paused: false,
            PausedReason: null,
            Blocked: false,
            BlockedReason: null,
            IsReconciliation: false,
            IterationSeedRotationCount: 0,
            AbortReason: "quorum lost");

        await publisher.PublishRoundStateChangedAsync(
            payload,
            sequence: 1,
            TestContext.Current.CancellationToken);

        CapturedPublish publish = Assert.Single(harness.Captured);
        Assert.Equal(OrchestratorRabbitMqTopology.RoundExchange, publish.Exchange);
        Assert.Equal("round.state.aborted", publish.RoutingKey);

        Envelope<RoundStateChangedPayload>? envelope =
            JsonSerializer.Deserialize<Envelope<RoundStateChangedPayload>>(
                publish.BodyAsString,
                CamelCaseOptions);
        Assert.NotNull(envelope);
        Assert.Equal("quorum lost", envelope!.Payload.AbortReason);
    }

    [Fact]
    public async Task RegimeForce_RoutesToQuoterMcExchange_NotBifrostMcV1()
    {
        // D-14 amendment gate: the orchestrator MUST NOT route RegimeForce to
        // bifrost.mc.v1 (audit-only). It routes to bifrost.mc / mc.regime.force
        // so the quoter's McRegimeForceConsumer picks it up and emits the
        // public Event.RegimeChange per Phase 03 D-17.
        RabbitMqSubscriberHarness harness = new();
        OrchestratorPublisher publisher = new(harness.Channel, new FakeClock());

        // Shape matches Bifrost.Quoter.Rabbit.McRegimeForceDto without the
        // ProjectReference — the harness only inspects the envelope routing,
        // not the DTO's field semantics.
        object regimeForcePayload = new { regime = "Volatile", nonce = Guid.NewGuid() };

        await publisher.PublishRegimeForceAsync(
            regimeForcePayload,
            TestContext.Current.CancellationToken);

        CapturedPublish publish = Assert.Single(harness.Captured);
        Assert.Equal(OrchestratorRabbitMqTopology.QuoterMcExchange, publish.Exchange);
        Assert.Equal("bifrost.mc", publish.Exchange); // literal gate — D-14 invariant
        Assert.Equal("mc.regime.force", publish.RoutingKey);
        Assert.NotEqual("bifrost.mc.v1", publish.Exchange);
    }
}
