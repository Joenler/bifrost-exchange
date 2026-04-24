using System.Collections.Frozen;
using System.Threading.Channels;
using Bifrost.Contracts.Internal.Auction;
using Bifrost.DahAuction.Commands;
using Bifrost.DahAuction.Rabbit;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoundStateEnum = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.DahAuction.State;

/// <summary>
/// Single-writer actor write-loop for the DAH auction service. Drains a
/// bounded <see cref="Channel{T}"/> of <see cref="IAuctionCommand"/> and
/// mutates a plain <see cref="Dictionary{TKey,TValue}"/> keyed on
/// (team_name, quarter_id). No locks; no concurrent-dictionary type:
/// compound operations (TryGetValue + mutate + store) on a thread-safe
/// dictionary type are not atomic, which would reintroduce the exact class
/// of data-race bug the actor-loop pattern exists to avoid. The bid map is
/// read/written ONLY inside <see cref="ExecuteAsync"/>'s drain body.
/// </summary>
/// <remarks>
/// Four command types processed:
///   - <see cref="SubmitBidCommand"/> — gate-check + map update +
///     <c>auction_bid</c> audit event.
///   - <see cref="ClearCommand"/> — snapshot the map, run per-QH clearing,
///     publish results. Body lives in <see cref="ProcessClearAsync"/>; the
///     real compute-and-publish driver lands in a follow-up commit. The
///     placeholder keeps the DI graph + integration-test fixtures exercising
///     every other code path.
///   - <see cref="ResetStateCommand"/> — clear map; <c>_acceptingBids</c> = false.
///     Idempotent.
///   - <see cref="OpenBidsCommand"/> — <c>_acceptingBids</c> = true.
///     Idempotent.
///
/// RoundState transitions are wired in the ctor via
/// <see cref="IRoundStateSource.OnChange"/>; the subscriber
/// <see cref="HandleRoundStateChange"/> posts the appropriate command onto
/// the channel and does NOT mutate state directly — the drain body is the
/// sole writer.
/// </remarks>
public sealed class AuctionWriteLoop : BackgroundService
{
    private readonly Channel<IAuctionCommand> _channel;
    private readonly IRoundStateSource _roundState;
    private readonly AuctionPublisher _publisher;
    private readonly IClock _clock;
    private readonly ILogger<AuctionWriteLoop> _log;

    // Single-writer state — only ExecuteAsync drain body mutates these.
    // DO NOT swap this for a thread-safe / concurrent-dictionary type:
    // compound operations (TryGetValue + mutate + store) on such a type are
    // not atomic, which would reintroduce the exact class of data-race bug
    // this actor loop exists to avoid. The bounded channel + single reader
    // gives us serialisation for free.
    private readonly Dictionary<(string Team, string Quarter), BidMatrixDto> _bids = new();
    private bool _acceptingBids;

    /// <summary>
    /// Boot state is derived from <see cref="IRoundStateSource.Current"/>;
    /// booting directly into <see cref="RoundStateEnum.AuctionOpen"/> accepts
    /// bids immediately, idempotent with a later explicit open transition
    /// that would also set <c>_acceptingBids = true</c>.
    /// </summary>
    public AuctionWriteLoop(
        Channel<IAuctionCommand> channel,
        IRoundStateSource roundState,
        AuctionPublisher publisher,
        IClock clock,
        ILogger<AuctionWriteLoop> log)
    {
        _channel = channel;
        _roundState = roundState;
        _publisher = publisher;
        _clock = clock;
        _log = log;

        _acceptingBids = _roundState.Current == RoundStateEnum.AuctionOpen;
        _roundState.OnChange += HandleRoundStateChange;
    }

    private void HandleRoundStateChange(object? sender, RoundStateChangedEventArgs e)
    {
        // Enqueue transitions — do NOT mutate _acceptingBids or _bids here.
        // The loop is the sole writer. TryWrite is fire-and-forget; the
        // channel is sized (ChannelCapacity = 256 by default) to dwarf
        // realistic round-state transition rates, so a TryWrite drop is
        // operationally impossible in Phase 05. If the channel ever does
        // fill up, the state-transition command is silently dropped — the
        // integration test in Plan 07 covers end-to-end lifecycle so any
        // regression would surface there.
        switch (e.Current)
        {
            case RoundStateEnum.AuctionOpen:
                _channel.Writer.TryWrite(new OpenBidsCommand());
                break;
            case RoundStateEnum.AuctionClosed:
                _channel.Writer.TryWrite(new ClearCommand(e.TimestampNs));
                break;
            case RoundStateEnum.IterationOpen:
                _channel.Writer.TryWrite(new ResetStateCommand());
                break;
            // RoundOpen, Gate, Settled, Aborted: no-op. _acceptingBids stays
            // false from the last ClearCommand (or ResetStateCommand on
            // restart), and the bid map is cleared on next IterationOpen.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "AuctionWriteLoop started — initial RoundState={RoundState}, AcceptingBids={Accepting}",
            _roundState.Current, _acceptingBids);

        try
        {
            await foreach (var cmd in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    switch (cmd)
                    {
                        case SubmitBidCommand submit:
                            ProcessSubmitBid(submit);
                            break;
                        case ClearCommand clear:
                            await ProcessClearAsync(clear, stoppingToken);
                            _acceptingBids = false;
                            break;
                        case ResetStateCommand:
                            _bids.Clear();
                            _acceptingBids = false;
                            break;
                        case OpenBidsCommand:
                            _acceptingBids = true;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "AuctionWriteLoop command failed: {Cmd}", cmd.GetType().Name);
                    // If the command carries a TCS, fail it so the HTTP
                    // handler unblocks with a typed error instead of
                    // hanging indefinitely. "Structural" is the closest
                    // fit in the fixed reject vocabulary for "internal loop
                    // exception"; Plan 07 integration tests may refine.
                    if (cmd is SubmitBidCommand failed)
                    {
                        failed.Completion.TrySetResult(new SubmitBidResult(
                            Accepted: false,
                            RejectCode: "Structural",
                            RejectDetail: "internal_error"));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on SIGTERM — IHostApplicationLifetime handles
            // propagation; the host will invoke StopAsync next.
        }
    }

    private void ProcessSubmitBid(SubmitBidCommand cmd)
    {
        if (!_acceptingBids)
        {
            cmd.Completion.TrySetResult(new SubmitBidResult(
                Accepted: false,
                RejectCode: "AuctionNotOpen",
                RejectDetail: $"current_state={_roundState.Current}"));
            return;
        }

        var key = (cmd.BidMatrix.TeamName, cmd.BidMatrix.QuarterId);
        // Replace-on-duplicate per the SPEC's in-memory map rule: a team's
        // later POST for the same QH supersedes any earlier submission.
        _bids[key] = cmd.BidMatrix;
        _publisher.PublishAuctionBidEvent(cmd.BidMatrix);
        cmd.Completion.TrySetResult(new SubmitBidResult(
            Accepted: true,
            RejectCode: null,
            RejectDetail: null));
    }

    // ProcessClearAsync body is intentionally minimal here — the real
    // snapshot + per-QH UniformPriceClearing.Compute + publish fan-out lands
    // in a subsequent commit. Keeping the method in place means the DI graph
    // and integration-test fixtures already exercise every other code path
    // through the loop; the follow-up swaps the body without touching the
    // rest of the file.
    private Task ProcessClearAsync(ClearCommand clear, CancellationToken ct)
    {
        // Snapshot for determinism: the clearing input is a frozen copy of
        // the bid map so any further mutation during clearing cannot affect
        // the result. The frozen-dictionary call is O(n) but n = teams * 4 QH
        // which is bounded by the registered-team count (≤ 8 per PROJECT.md)
        // times 4, so at worst ~32 entries — negligible.
        var snapshot = _bids.ToFrozenDictionary();
        _log.LogWarning(
            "AuctionWriteLoop.ProcessClearAsync placeholder — snapshot size={Count}, ClearCommand at {Ts}ns. Real clearing driver is a follow-up.",
            snapshot.Count, clear.SnapshotTimestampNs);
        _ = _clock; // suppress analyzer noise: _clock is reserved for the clearing driver
        _ = ct;
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _roundState.OnChange -= HandleRoundStateChange;
        // TryComplete is idempotent if someone else already completed the
        // channel writer (e.g. on a double-stop).
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}
