using Bifrost.Exchange.Domain;

namespace Bifrost.Exchange.Application;

public readonly record struct OrderValidationResult
{
    public bool IsValid { get; init; }
    public string? RejectionReason { get; init; }
    public RejectionCode? Code { get; init; }
    public Side Side { get; init; }
    public OrderType OrderType { get; init; }
    public InstrumentId InstrumentId { get; init; }
    public MatchingEngine? Engine { get; init; }

    public static OrderValidationResult Rejected(RejectionCode code, string reason) =>
        new() { RejectionReason = reason, Code = code };

    public static OrderValidationResult Valid(
        Side side, OrderType orderType,
        InstrumentId instrumentId, MatchingEngine engine) =>
        new()
        {
            IsValid = true, Side = side, OrderType = orderType,
            InstrumentId = instrumentId, Engine = engine
        };
}
