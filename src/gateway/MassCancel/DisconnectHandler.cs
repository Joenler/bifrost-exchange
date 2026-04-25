using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Gateway.Rabbit;
using Bifrost.Gateway.State;
using Bifrost.Time;
using Microsoft.Extensions.Logging;

namespace Bifrost.Gateway.MassCancel;

/// <summary>
/// GW-07 mass-cancel-on-disconnect handler. Snapshot the team's resting orders
/// under <see cref="TeamState.StateLock"/>, RELEASE the lock, then fire a
/// <see cref="CancelOrderCommand"/> fleet via <see cref="Task.WhenAll"/> on the
/// dedicated <see cref="IGatewayCommandPublisher"/>.
///
/// CALLER must pass a FRESH <see cref="CancellationToken"/> — Pitfall 5: the
/// per-stream token is already cancelled when <c>StreamStrategy.finally</c>
/// runs, so passing <c>context.CancellationToken</c> would short-circuit every
/// publish before the AMQP frame is on the wire. The two call sites both honor
/// this:
/// <list type="bullet">
///   <item><see cref="Streaming.StrategyGatewayService"/> uses a fresh 2-second CTS in finally.</item>
///   <item><c>Program.cs</c> registers an <c>IHostApplicationLifetime.ApplicationStopping</c>
///   callback with a fresh 5-second CTS for SIGTERM (Open Question 2 closure).</item>
/// </list>
///
/// Design note (lock discipline): we snapshot OpenOrdersByInstrument under the
/// lock, then CLEAR the per-instrument lists inline. Even if a publish later
/// fails, the gateway's view of the team is now "no resting orders" — so a
/// subsequent reconnect from the same team starts from a clean slate. The
/// authoritative order book lives in the matching engine; this clear is purely
/// the gateway's bookkeeping.
/// </summary>
public sealed class DisconnectHandler
{
    private readonly IGatewayCommandPublisher _publisher;
    private readonly IClock _clock;
    private readonly ILogger<DisconnectHandler> _log;

    public DisconnectHandler(IGatewayCommandPublisher publisher, IClock clock, ILogger<DisconnectHandler> log)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Mass-cancel every resting order this team has. Returns once all publish
    /// awaitables complete, or the <paramref name="ct"/> fires (whichever first).
    /// Errors are logged; never thrown — disconnect path must remain best-effort.
    /// </summary>
    public async Task HandleAsync(TeamState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 1. Snapshot resting orders under team lock; clear inline so the
        //    gateway's view is clean even if a subsequent publish fails.
        (long OrderId, int InstrumentIndex)[] resting;
        lock (state.StateLock)
        {
            var total = 0;
            for (var i = 0; i < state.OpenOrdersByInstrument.Length; i++)
                total += state.OpenOrdersByInstrument[i].Count;
            resting = new (long, int)[total];
            var idx = 0;
            for (var i = 0; i < state.OpenOrdersByInstrument.Length; i++)
            {
                var list = state.OpenOrdersByInstrument[i];
                for (var k = 0; k < list.Count; k++)
                    resting[idx++] = (list[k].OrderId, i);
            }
            // Clear the open-orders maps inline. The cancel publishes are best-effort
            // fire-and-forget; even if any fail, the gateway's view of the team is
            // now "no resting orders".
            for (var i = 0; i < state.OpenOrdersByInstrument.Length; i++)
                state.OpenOrdersByInstrument[i].Clear();
        }

        if (resting.Length == 0)
        {
            _log.LogDebug("DisconnectHandler: team {Team} had no open orders", state.TeamName);
            return;
        }

        // 2. Fire all cancels in parallel. Task.WhenAll on async publishes — channel-level
        //    frame serialization is handled AMQP-side. Sequential 50 × 20ms = 1s SLO target;
        //    parallel collapses that to single-digit ms.
        var startedUtc = _clock.GetUtcNow();
        var tasks = new List<Task>(resting.Length);
        for (var i = 0; i < resting.Length; i++)
        {
            var r = resting[i];
            // Map the gateway's slot index back to the wire DTO so the matching engine
            // can resolve the instrument unambiguously.
            var instrumentDto = InstrumentOrdering.DtoFor(r.InstrumentIndex);
            var cmd = new CancelOrderCommand(
                ClientId: state.ClientId,
                OrderId: r.OrderId,
                InstrumentId: instrumentDto);
            var corr = $"mass-cancel-{state.ClientId}-{r.OrderId}";
            tasks.Add(_publisher.PublishCancelOrderAsync(state.ClientId, cmd, corr, ct).AsTask());
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(ct);
            var elapsedMs = (_clock.GetUtcNow() - startedUtc).TotalMilliseconds;
            _log.LogInformation(
                "Mass-cancel team={Team} count={Count} elapsedMs={Elapsed:F1}",
                state.TeamName, resting.Length, elapsedMs);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Mass-cancel team={Team} cancelled before completion (likely SIGTERM budget exhausted)",
                state.TeamName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Mass-cancel team={Team} encountered errors — some publishes may have failed",
                state.TeamName);
        }
    }

    /// <summary>
    /// SIGTERM defensive path (Open Question 2 closure). Fires
    /// <see cref="HandleAsync"/> for every team in parallel. Any single team's
    /// failure is logged inside <see cref="HandleAsync"/>; this aggregator
    /// awaits all in parallel under a single budget.
    /// </summary>
    public async Task HandleAllAsync(IEnumerable<TeamState> teams, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(teams);
        var tasks = new List<Task>();
        foreach (var t in teams)
        {
            tasks.Add(HandleAsync(t, ct));
        }
        if (tasks.Count == 0) return;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Mass-cancel HandleAllAsync cancelled before all teams completed");
        }
    }
}
