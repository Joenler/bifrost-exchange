using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// Outcome of a single guard check. Hardcoded sentinel <see cref="Ok"/> for the
/// accepted case avoids allocating a fresh record per passing guard call (hot path).
/// Per ADR-0004 + SPEC req 4: first-failure short-circuits later guards in the chain.
/// </summary>
public sealed record GuardResult(bool Accepted, StrategyProto.RejectReason Reason, string Detail)
{
    public static readonly GuardResult Ok = new(true, StrategyProto.RejectReason.Unspecified, string.Empty);

    public static GuardResult Reject(StrategyProto.RejectReason reason, string detail) =>
        new(false, reason, detail);
}
