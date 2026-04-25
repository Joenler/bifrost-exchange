using System.Diagnostics;
using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Gateway.Guards;
using Bifrost.Gateway.MassCancel;
using Bifrost.Gateway.Rabbit;
using Bifrost.Gateway.State;
using Bifrost.Gateway.Translation;
using Bifrost.Time;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AppRoundState = Bifrost.Exchange.Application.RoundState;
using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;
using MarketProto = Bifrost.Contracts.Market;

namespace Bifrost.Gateway.Streaming;

/// <summary>
/// Bidi gRPC handler for <c>StrategyGatewayService.StreamStrategy</c>. Implements the
/// canonical reader-task + writer-task + bounded-channel pattern from
/// 07-RESEARCH §Pattern 1 (Microsoft's recommended bidi shape) and ADR-0002.
///
/// Per <see cref="StreamContext"/>: ALL outbound traffic flows through
/// <c>StreamContext.Outbound</c>; only the writer task spawned here calls
/// <c>responseStream.WriteAsync</c> (Pitfall 2 — IServerStreamWriter is single-thread).
///
/// Pitfall 5: the <c>finally</c> block uses a fresh 2-second
/// <see cref="CancellationTokenSource"/> — NEVER the per-stream token — so the
/// disconnect handler can complete its mass-cancel publishing even after the
/// stream is already cancelled. Plan 07 lands the real handler; this plan
/// ships a <c>DetachOnly</c> stub that detaches the outbound writer + marks
/// the team disconnected so the swap is a one-line change.
///
/// Plan 06 wires the inbound RabbitMQ consumers that drive the outbound channel.
/// </summary>
public sealed class StrategyGatewayService : StrategyProto.StrategyGatewayService.StrategyGatewayServiceBase
{
    private readonly TeamRegistry _registry;
    private readonly GuardThresholds _thresholds;
    private readonly IClock _clock;
    private readonly IGatewayCommandPublisher _cmdPublisher;
    private readonly AppRoundState.IRoundStateSource _roundState;
    private readonly DisconnectHandler _disconnect;
    private readonly int _outboundCapacity;
    private readonly ILogger<StrategyGatewayService> _log;

    public StrategyGatewayService(
        TeamRegistry registry,
        GuardThresholds thresholds,
        IClock clock,
        IGatewayCommandPublisher cmdPublisher,
        AppRoundState.IRoundStateSource roundState,
        DisconnectHandler disconnect,
        IConfiguration config,
        ILogger<StrategyGatewayService> log)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cmdPublisher = cmdPublisher ?? throw new ArgumentNullException(nameof(cmdPublisher));
        _roundState = roundState ?? throw new ArgumentNullException(nameof(roundState));
        _disconnect = disconnect ?? throw new ArgumentNullException(nameof(disconnect));
        ArgumentNullException.ThrowIfNull(config);
        _outboundCapacity = config.GetValue("Gateway:OutboundChannelCapacity", 1024);
        if (_outboundCapacity <= 0) _outboundCapacity = 1024;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public override async Task StreamStrategy(
        IAsyncStreamReader<StrategyProto.StrategyCommand> requestStream,
        IServerStreamWriter<StrategyProto.MarketEvent> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);

        var ct = context.CancellationToken;

        // 1. First-frame handshake (SPEC req 1).
        if (!await requestStream.MoveNext(ct))
        {
            _log.LogInformation("Stream closed before first frame");
            return;
        }
        var first = requestStream.Current;
        if (first.CommandCase != StrategyProto.StrategyCommand.CommandOneofCase.Register)
        {
            _log.LogWarning("First frame {Case} is not Register; closing with no reply (SPEC req 1)", first.CommandCase);
            return;
        }

        var teamName = first.Register.TeamName;
        var lastSeen = first.Register.LastSeenSequence;
        var registerResult = _registry.TryRegister(teamName, lastSeen);
        if (!registerResult.Success || registerResult.TeamState is null)
        {
            // Reserved-id rejection or empty/whitespace team_name. Per SPEC req 9:
            // emit OrderReject(STRUCTURAL) and close — clearer than reregister_required
            // for a pre-registration rejection.
            await responseStream.WriteAsync(
                OutboundTranslator.BuildOrderReject(
                    StrategyProto.RejectReason.Structural,
                    registerResult.FailureDetail ?? "register failed"),
                ct);
            return;
        }

        var teamState = registerResult.TeamState;
        using var streamCtx = new StreamContext(teamState, _outboundCapacity, ct);

        // 2. Attach outbound writer to TeamState so RabbitMQ consumers (Plan 06) and
        //    ForecastDispatcher (Plan 07) can push events at this stream.
        lock (teamState.StateLock) { teamState.AttachOutbound(streamCtx.Outbound.Writer); }

        try
        {
            // 3. Build RegisterAck + 5-instrument PositionSnapshot burst (D-06a) +
            //    optional resume slice (Pitfall 10: snapshot under lock, write outside).
            var roundProtoState = MapDomainToProtoRoundState(_roundState.Current);
            var roundMessage = new RoundProto.RoundState
            {
                State = roundProtoState,
                RoundNumber = 0,
                ScenarioSeed = 0,
                TransitionNs = 0,
                ExpectedNextTransitionNs = 0,
            };

            await streamCtx.Outbound.Writer.WriteAsync(
                OutboundTranslator.BuildRegisterAck(
                    teamState.ClientId,
                    roundMessage,
                    registerResult.ResumedFromSequence,
                    registerResult.ReregisterRequired),
                ct);

            // 5 PositionSnapshots in canonical instrument order (D-06a).
            (string Id, long Net, long Vwap, long Notional)[] burst;
            lock (teamState.StateLock)
            {
                burst = new (string, long, long, long)[InstrumentOrdering.Slots];
                for (var i = 0; i < InstrumentOrdering.Slots; i++)
                {
                    burst[i] = (
                        InstrumentOrdering.CanonicalIds[i],
                        teamState.NetPositionTicks[i],
                        teamState.VwapTicks[i],
                        teamState.OpenOrdersNotionalTicks[i]);
                }
            }
            for (var i = 0; i < burst.Length; i++)
            {
                var b = burst[i];
                await streamCtx.Outbound.Writer.WriteAsync(
                    OutboundTranslator.BuildPositionSnapshot(
                        instrumentId: SyntheticInstrumentDto(b.Id),
                        instrumentIdString: b.Id,
                        productType: MarketProto.ProductType.Unspecified,
                        netPositionTicks: b.Net,
                        averagePriceTicks: b.Vwap,
                        openOrdersNotionalTicks: b.Notional),
                    ct);
            }

            // Optional resume slice (Pitfall 10: take a snapshot under the lock, release,
            // then push to the channel).
            Envelope<object>[] resumeSlice;
            lock (teamState.StateLock)
            {
                resumeSlice = registerResult.ResumedFromSequence > 0
                    ? teamState.Ring.SnapshotFrom(registerResult.ResumedFromSequence)
                    : Array.Empty<Envelope<object>>();
            }
            for (var i = 0; i < resumeSlice.Length; i++)
            {
                if (resumeSlice[i].Payload is StrategyProto.MarketEvent me)
                {
                    await streamCtx.Outbound.Writer.WriteAsync(me, ct);
                }
            }

            // 4. Spawn writer task — the SOLE writer of responseStream (Pitfall 2).
            var writerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var evt in streamCtx.Outbound.Reader.ReadAllAsync(streamCtx.StreamCts.Token))
                    {
                        try
                        {
                            await responseStream.WriteAsync(evt, streamCtx.StreamCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Outbound write failed for team {Team}", teamState.TeamName);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { /* clean disconnect */ }
            }, streamCtx.StreamCts.Token);

            // 5. Reader loop — single-thread consumer of requestStream is safe per gRPC docs.
            try
            {
                while (await requestStream.MoveNext(ct))
                {
                    var cmd = requestStream.Current;
                    var sw = Stopwatch.StartNew();
                    await HandleCommandAsync(teamState, cmd, streamCtx.Outbound.Writer, ct);
                    // Plan 08 metrics observation:
                    // GatewayMetrics.StreamLatency.WithLabels(teamState.TeamName)
                    //                             .Observe(sw.Elapsed.TotalSeconds);
                    _ = sw;
                }
            }
            catch (OperationCanceledException) { /* clean disconnect */ }

            streamCtx.Outbound.Writer.TryComplete();
            await writerTask;
        }
        finally
        {
            // 6. Mass-cancel SLO target is 1 s (GW-07). Pitfall 5: NEVER use the per-stream
            //    `ct` here — it is already cancelled. The DisconnectHandler runs under a
            //    FRESH 2-second CTS so the cancel-fleet publishes can complete even after
            //    the team's stream has already torn down.
            lock (teamState.StateLock) { teamState.DetachOutbound(); }
            _registry.MarkDisconnected(teamState);

            using var cancelCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _disconnect.HandleAsync(teamState, cancelCts.Token);
            }
            catch (Exception ex)
            {
                // The handler swallows publish errors itself; this catch only
                // protects against an unexpected exception propagating out and
                // surfacing as a gRPC handler crash on the disconnect path.
                _log.LogError(ex, "DisconnectHandler.HandleAsync threw for team {Team}", teamState.TeamName);
            }
        }
    }

    private async Task HandleCommandAsync(
        TeamState state,
        StrategyProto.StrategyCommand cmd,
        ChannelWriter<StrategyProto.MarketEvent> outbound,
        CancellationToken ct)
    {
        // Guard chain — every command runs the full Plan 04 chain under StateLock.
        // The chain rejects reserved client_ids (StructuralGuard) so no extra
        // boundary check is needed here. ADR-0002 + ADR-0004 + Phase 02 D-09:
        // OrderCancel bypasses Tier 3-5 inside GuardChain.
        GuardResult guardResult;
        var roundEnum = MapDomainToProtoRoundState(_roundState.Current);
        lock (state.StateLock)
        {
            guardResult = GuardChain.Evaluate(state, cmd, _clock, roundEnum, _thresholds);
        }
        if (!guardResult.Accepted)
        {
            await outbound.WriteAsync(
                OutboundTranslator.BuildOrderReject(guardResult.Reason, guardResult.Detail),
                ct);
            return;
        }

        // Translate accepted commands + publish on bifrost.cmd via the dedicated
        // GatewayCommandPublisher channel (Pitfall 6).
        var correlationId = $"gw-{state.ClientId}-{Guid.NewGuid():N}";
        switch (cmd.CommandCase)
        {
            case StrategyProto.StrategyCommand.CommandOneofCase.OrderSubmit:
            {
                var dto = InboundTranslator.ToInternalSubmit(cmd.OrderSubmit, state.ClientId);
                await _cmdPublisher.PublishSubmitOrderAsync(state.ClientId, dto, correlationId, ct);
                break;
            }
            case StrategyProto.StrategyCommand.CommandOneofCase.OrderCancel:
            {
                var dto = InboundTranslator.ToInternalCancel(cmd.OrderCancel, state.ClientId);
                await _cmdPublisher.PublishCancelOrderAsync(state.ClientId, dto, correlationId, ct);
                break;
            }
            case StrategyProto.StrategyCommand.CommandOneofCase.OrderReplace:
            {
                var dto = InboundTranslator.ToInternalReplace(cmd.OrderReplace, state.ClientId);
                await _cmdPublisher.PublishReplaceOrderAsync(state.ClientId, dto, correlationId, ct);
                break;
            }
            // BidMatrixSubmit and mid-stream Register were rejected by StructuralGuard.
        }
    }

    /// <summary>
    /// Maps the domain <c>Bifrost.Exchange.Application.RoundState.RoundState</c> enum
    /// (the seam IRoundStateSource.Current exposes) to the proto
    /// <c>Bifrost.Contracts.Round.State</c> enum the GuardChain expects. Required
    /// because the gateway sits between the central-machine seam and the team-facing
    /// proto — both shapes carry the same 7 logical states.
    /// </summary>
    public static RoundProto.State MapDomainToProtoRoundState(AppRoundState.RoundState s) => s switch
    {
        AppRoundState.RoundState.IterationOpen => RoundProto.State.IterationOpen,
        AppRoundState.RoundState.AuctionOpen => RoundProto.State.AuctionOpen,
        AppRoundState.RoundState.AuctionClosed => RoundProto.State.AuctionClosed,
        AppRoundState.RoundState.RoundOpen => RoundProto.State.RoundOpen,
        AppRoundState.RoundState.Gate => RoundProto.State.Gate,
        AppRoundState.RoundState.Settled => RoundProto.State.Settled,
        AppRoundState.RoundState.Aborted => RoundProto.State.Aborted,
        _ => RoundProto.State.Unspecified,
    };

    /// <summary>
    /// Construct a synthetic <see cref="InstrumentIdDto"/> for the canonical 5-instrument
    /// PositionSnapshot burst on RegisterAck (D-06a). The catalog wiring lives in Plan 06;
    /// for Plan 05 we mirror the static <c>TradingCalendar</c> (5-instrument single-area
    /// 9999-01-01 synthetic delivery shape) so the proto fields are well-formed.
    /// </summary>
    private static InstrumentIdDto SyntheticInstrumentDto(string instrumentId)
    {
        // Single area "DE" + synthetic 9999 delivery (matches Phase 02 TradingCalendar).
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return instrumentId switch
        {
            "H1" => new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1)),
            "Q1" => new InstrumentIdDto("DE", hourStart, hourStart.AddMinutes(15)),
            "Q2" => new InstrumentIdDto("DE", hourStart.AddMinutes(15), hourStart.AddMinutes(30)),
            "Q3" => new InstrumentIdDto("DE", hourStart.AddMinutes(30), hourStart.AddMinutes(45)),
            "Q4" => new InstrumentIdDto("DE", hourStart.AddMinutes(45), hourStart.AddHours(1)),
            _ => new InstrumentIdDto("DE", hourStart, hourStart.AddHours(1)),
        };
    }
}
