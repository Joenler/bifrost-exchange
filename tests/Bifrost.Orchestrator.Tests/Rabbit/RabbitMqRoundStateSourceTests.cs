using System.Text;
using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bifrost.Orchestrator.Tests.Rabbit;

/// <summary>
/// Unit-coverage for the production <see cref="RabbitMqRoundStateSource"/> seam impl.
/// The end-to-end wire path (publisher → broker → subscriber) is exercised by Plan
/// 06-05's <see cref="RoundStateChangedPublishTests"/> (publish side) and the actor-loop
/// stress test (round-trip side); these facts pin the deserialise-and-update logic
/// directly so a future regression that drifts the JSON shape, the state-name parse,
/// or the reconciliation-re-raise rule fails locally without a broker.
/// </summary>
public sealed class RabbitMqRoundStateSourceTests
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void HandleMessageBytes_NormalTransition_UpdatesCurrent_AndRaisesOnChange()
    {
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RabbitMqRoundStateSource source = new(harness.Channel, clock, NullLogger<RabbitMqRoundStateSource>.Instance);

        // Pre-condition: source defaults to IterationOpen until first message lands.
        Assert.Equal(RoundState.IterationOpen, source.Current);

        RoundStateChangedEventArgs? captured = null;
        source.OnChange += (_, args) => captured = args;

        byte[] body = BuildEnvelopeBytes(state: "AuctionOpen", isReconciliation: false);

        source.HandleMessageBytes(body);

        Assert.Equal(RoundState.AuctionOpen, source.Current);
        Assert.NotNull(captured);
        Assert.Equal(RoundState.IterationOpen, captured!.Previous);
        Assert.Equal(RoundState.AuctionOpen, captured.Current);
        Assert.True(captured.TimestampNs > 0);
    }

    [Fact]
    public void HandleMessageBytes_ReconciliationOnSameState_StillRaisesOnChange()
    {
        // SPEC Req 4 + plan body note: "IsReconciliation=true messages should still raise
        // OnChange so downstream consumers flip their local Current to match, even if the
        // state name is the same as the previous value." Drive the source twice with the
        // same state — the second call carries IsReconciliation=true and must re-raise.
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RabbitMqRoundStateSource source = new(harness.Channel, clock, NullLogger<RabbitMqRoundStateSource>.Instance);

        source.HandleMessageBytes(BuildEnvelopeBytes(state: "RoundOpen", isReconciliation: false));

        int onChangeCount = 0;
        source.OnChange += (_, _) => onChangeCount++;

        // Second message: SAME state, but IsReconciliation=true. OnChange MUST still fire.
        source.HandleMessageBytes(BuildEnvelopeBytes(state: "RoundOpen", isReconciliation: true));

        Assert.Equal(1, onChangeCount);
        Assert.Equal(RoundState.RoundOpen, source.Current);
    }

    [Fact]
    public void HandleMessageBytes_UnrecognisedStateName_DoesNotThrow_OrChangeCurrent_OrRaiseOnChange()
    {
        // Defensive parse: a future RoundState enum addition on the publisher side that
        // hasn't reached this consumer yet must not crash the loop. The handler logs at
        // warning level and skips the message.
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RabbitMqRoundStateSource source = new(harness.Channel, clock, NullLogger<RabbitMqRoundStateSource>.Instance);

        bool onChangeFired = false;
        source.OnChange += (_, _) => onChangeFired = true;

        byte[] body = BuildEnvelopeBytes(state: "TotallyUnknownState", isReconciliation: false);

        source.HandleMessageBytes(body);

        Assert.Equal(RoundState.IterationOpen, source.Current);
        Assert.False(onChangeFired);
    }

    [Fact]
    public void HandleMessageBytes_MalformedJson_DoesNotThrow()
    {
        RabbitMqSubscriberHarness harness = new();
        FakeClock clock = new();
        RabbitMqRoundStateSource source = new(harness.Channel, clock, NullLogger<RabbitMqRoundStateSource>.Instance);

        byte[] garbage = Encoding.UTF8.GetBytes("{ not valid json");

        // Must not throw — the exception path is logged and swallowed so a single bad
        // message can't take the consumer's poll loop down.
        source.HandleMessageBytes(garbage);

        Assert.Equal(RoundState.IterationOpen, source.Current);
    }

    private static byte[] BuildEnvelopeBytes(string state, bool isReconciliation)
    {
        RoundStateChangedPayload payload = new(
            State: state,
            RoundNumber: 1,
            ScenarioSeedOnWire: 0,
            TransitionNs: 1714516800000000000L,
            ExpectedNextTransitionNs: null,
            Paused: false,
            PausedReason: null,
            Blocked: false,
            BlockedReason: null,
            IsReconciliation: isReconciliation,
            IterationSeedRotationCount: 0,
            AbortReason: null);

        // Fixed timestamp — test asserts on payload semantics, not envelope wall-clock.
        DateTimeOffset fixedUtc = new(2026, 4, 25, 0, 0, 0, TimeSpan.Zero);
        Envelope<RoundStateChangedPayload> envelope = new(
            MessageType: MessageTypes.RoundStateChanged,
            TimestampUtc: fixedUtc,
            CorrelationId: null,
            ClientId: null,
            InstrumentId: null,
            Sequence: 1,
            Payload: payload);

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, CamelCaseOptions));
    }
}
