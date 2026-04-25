using Bifrost.Contracts.Internal.Commands;
using Bifrost.Gateway.MassCancel;
using Bifrost.Gateway.Rabbit;
using Bifrost.Gateway.State;
using Bifrost.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Gateway.Tests.MassCancel;

/// <summary>
/// Unit tests for <see cref="DisconnectHandler"/> covering the GW-07
/// mass-cancel-on-disconnect contract:
/// <list type="bullet">
///   <item>No publishes when team has no resting orders.</item>
///   <item>One publish per resting order across multiple instruments.</item>
///   <item>Cancel-fleet honours the caller's CancellationToken (Pitfall 5 budget).</item>
///   <item>HandleAllAsync fans out across many teams in parallel.</item>
///   <item>OpenOrdersByInstrument is cleared after the snapshot+release pattern.</item>
/// </list>
/// All tests use a recording <see cref="IGatewayCommandPublisher"/> stub —
/// no RabbitMQ broker required.
/// </summary>
public class DisconnectHandlerTests
{
    private static TestClock NewClock() => new(new FakeTimeProvider(new DateTimeOffset(2026, 4, 25, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task HandleAsync_NoOpenOrders_NoPublishes()
    {
        var pub = new RecordingPublisher();
        var handler = new DisconnectHandler(pub, NewClock(), NullLogger<DisconnectHandler>.Instance);
        var state = new TeamState("alpha", "team-alpha-1", default);

        await handler.HandleAsync(state, CancellationToken.None);

        Assert.Empty(pub.Cancels);
    }

    [Fact]
    public async Task HandleAsync_With10OpenOrdersAcross3Instruments_Publishes10Cancels()
    {
        var pub = new RecordingPublisher();
        var handler = new DisconnectHandler(pub, NewClock(), NullLogger<DisconnectHandler>.Instance);
        var state = new TeamState("alpha", "team-alpha-1", default);

        // 10 open orders across slots H1=4, Q1=3, Q2=3.
        var added = 0;
        foreach (var (instIdx, count) in new[] { (0, 4), (1, 3), (2, 3) })
        {
            for (var k = 0; k < count; k++)
            {
                added++;
                state.OpenOrdersByInstrument[instIdx].Add(new OpenOrder(
                    OrderId: added,
                    ClientOrderId: $"co-{added}",
                    InstrumentIndex: instIdx,
                    Side: "Buy",
                    PriceTicks: 100,
                    QuantityTicks: 1,
                    DisplaySliceTicks: 0,
                    SubmittedAtUtc: default));
            }
        }

        await handler.HandleAsync(state, CancellationToken.None);

        Assert.Equal(10, pub.Cancels.Count);
        // Open-order maps should now be cleared.
        Assert.All(state.OpenOrdersByInstrument, list => Assert.Empty(list));
        // Every cancel command should carry the correct ClientId + an instrument DTO.
        Assert.All(pub.Cancels, t =>
        {
            Assert.Equal("team-alpha-1", t.ClientId);
            Assert.NotNull(t.Cmd.InstrumentId);
            Assert.StartsWith("mass-cancel-team-alpha-1-", t.CorrelationId);
        });
    }

    [Fact]
    public async Task HandleAsync_RespectsCancellationToken()
    {
        var slowPub = new SlowPublisher(perPublishMs: 200);
        var handler = new DisconnectHandler(slowPub, NewClock(), NullLogger<DisconnectHandler>.Instance);
        var state = new TeamState("alpha", "team-alpha-1", default);
        for (var i = 0; i < 100; i++)
        {
            state.OpenOrdersByInstrument[0].Add(new OpenOrder(
                OrderId: i,
                ClientOrderId: $"co-{i}",
                InstrumentIndex: 0,
                Side: "Buy",
                PriceTicks: 100,
                QuantityTicks: 1,
                DisplaySliceTicks: 0,
                SubmittedAtUtc: default));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Should NOT throw — the handler logs and returns within budget.
        await handler.HandleAsync(state, cts.Token);

        // No assertions on publish state — we're verifying graceful cancellation only.
    }

    [Fact]
    public async Task HandleAllAsync_ParallelMassCancel_AcrossTeams()
    {
        var pub = new RecordingPublisher();
        var handler = new DisconnectHandler(pub, NewClock(), NullLogger<DisconnectHandler>.Instance);
        var teams = Enumerable.Range(0, 8).Select(i =>
        {
            var s = new TeamState($"team-{i}", $"client-{i}", default);
            s.OpenOrdersByInstrument[0].Add(new OpenOrder(
                OrderId: i,
                ClientOrderId: $"co-{i}",
                InstrumentIndex: 0,
                Side: "Buy",
                PriceTicks: 100,
                QuantityTicks: 1,
                DisplaySliceTicks: 0,
                SubmittedAtUtc: default));
            return s;
        }).ToArray();

        await handler.HandleAllAsync(teams, CancellationToken.None);

        Assert.Equal(8, pub.Cancels.Count);
        // Every team's open-order map should be cleared.
        Assert.All(teams, t => Assert.All(t.OpenOrdersByInstrument, list => Assert.Empty(list)));
    }

    [Fact]
    public async Task HandleAsync_OpenOrdersClearedBeforePublishesComplete()
    {
        // Verifies the snapshot-then-release pattern: the lock is released BEFORE
        // the cancel publishes start, so an inspector reading state mid-publish sees
        // a cleared open-orders map.
        var slowPub = new SlowPublisher(perPublishMs: 50);
        var handler = new DisconnectHandler(slowPub, NewClock(), NullLogger<DisconnectHandler>.Instance);
        var state = new TeamState("alpha", "team-alpha-1", default);
        state.OpenOrdersByInstrument[0].Add(new OpenOrder(
            OrderId: 1,
            ClientOrderId: "co-1",
            InstrumentIndex: 0,
            Side: "Buy",
            PriceTicks: 100,
            QuantityTicks: 1,
            DisplaySliceTicks: 0,
            SubmittedAtUtc: default));

        var task = handler.HandleAsync(state, CancellationToken.None);

        // Briefly yield then check the state under the lock — should be empty already.
        await Task.Yield();
        lock (state.StateLock)
        {
            Assert.All(state.OpenOrdersByInstrument, list => Assert.Empty(list));
        }
        await task;
    }

    [Fact]
    public async Task HandleAllAsync_EmptyEnumerable_ReturnsImmediately()
    {
        var pub = new RecordingPublisher();
        var handler = new DisconnectHandler(pub, NewClock(), NullLogger<DisconnectHandler>.Instance);

        await handler.HandleAllAsync(Array.Empty<TeamState>(), CancellationToken.None);

        Assert.Empty(pub.Cancels);
    }

    private sealed class TestClock(FakeTimeProvider time) : IClock
    {
        public DateTimeOffset GetUtcNow() => time.GetUtcNow();
    }

    private sealed class RecordingPublisher : IGatewayCommandPublisher
    {
        public List<(string ClientId, CancelOrderCommand Cmd, string CorrelationId)> Cancels { get; } = new();

        public ValueTask PublishSubmitOrderAsync(string clientId, SubmitOrderCommand cmd, string correlationId, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask PublishCancelOrderAsync(string clientId, CancelOrderCommand cmd, string correlationId, CancellationToken ct = default)
        {
            lock (Cancels)
            {
                Cancels.Add((clientId, cmd, correlationId));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishReplaceOrderAsync(string clientId, ReplaceOrderCommand cmd, string correlationId, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class SlowPublisher(int perPublishMs) : IGatewayCommandPublisher
    {
        public ValueTask PublishSubmitOrderAsync(string clientId, SubmitOrderCommand cmd, string correlationId, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public async ValueTask PublishCancelOrderAsync(string clientId, CancelOrderCommand cmd, string correlationId, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(perPublishMs, ct);
            }
            catch (OperationCanceledException) { /* graceful */ }
        }

        public ValueTask PublishReplaceOrderAsync(string clientId, ReplaceOrderCommand cmd, string correlationId, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
