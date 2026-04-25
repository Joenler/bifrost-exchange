using RoundProto = Bifrost.Contracts.Round;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Tier 2 of the ADR-0004 chain. Submits + Replaces require <c>RoundOpen</c>;
/// Cancel commands bypass the state gate per ADR-0004 cancel-bypass invariant
/// + Phase 02 D-09 — teams must always be able to flatten exposure regardless
/// of round state. Failure rejects with REJECT_REASON_EXCHANGE_CLOSED.
/// </summary>
internal static class StateGateGuard
{
    public static GuardResult Check(StrategyProto.StrategyCommand cmd, RoundProto.State round)
    {
        // Cancel-bypass: cancel allowed in every state (ADR-0004 + Phase 02 D-09).
        if (cmd.CommandCase == StrategyProto.StrategyCommand.CommandOneofCase.OrderCancel)
            return GuardResult.Ok;

        if (round != RoundProto.State.RoundOpen)
            return GuardResult.Reject(StrategyProto.RejectReason.ExchangeClosed,
                $"command type {cmd.CommandCase} requires RoundOpen but RoundState is {round}");
        return GuardResult.Ok;
    }
}
