using Bifrost.Contracts.Internal.Auction;
using Bifrost.Exchange.Application;

namespace Bifrost.DahAuction.Validation;

/// <summary>
/// Synchronous, pure validator for bid-matrix submissions. Enforces the 6
/// validator-layer rejection codes (the 7th — AuctionNotOpen — lives in the
/// actor loop because it needs access to the live <c>_acceptingBids</c> flag
/// driven by <c>IRoundStateSource</c> transitions).
/// </summary>
/// <remarks>
/// Called from the HTTP endpoint handler BEFORE the <see cref="BidMatrixDto"/>
/// is handed to the actor channel. Fast-fail keeps malformed payloads out of
/// the bounded channel's back-pressure path.
///
/// Ordinal string compares throughout; no culture-dependent parsing. Prices may
/// be negative (Nordic / CE DAH convention); only quantities are strictly
/// positive. Buy steps strictly descending by price (willingness-to-pay
/// decreases with step index); sell steps strictly ascending.
/// </remarks>
public sealed class BidMatrixValidator
{
    private readonly InstrumentRegistry _registry;
    private readonly int _maxStepsPerSide;

    public BidMatrixValidator(InstrumentRegistry registry, int maxStepsPerSide)
    {
        _registry = registry;
        _maxStepsPerSide = maxStepsPerSide;
    }

    public ValidationResult<BidMatrixDto, BidMatrixValidationError> Validate(BidMatrixDto candidate)
    {
        // 1. UnknownTeam: empty / whitespace team_name.
        if (string.IsNullOrEmpty(candidate.TeamName))
        {
            return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                new BidMatrixValidationError("UnknownTeam", "empty_team_name"));
        }
        if (string.IsNullOrWhiteSpace(candidate.TeamName))
        {
            return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                new BidMatrixValidationError("UnknownTeam", "whitespace_team_name"));
        }

        // 2. UnknownQuarter: quarter_id must match one of the 4 QH instruments.
        //    The hour instrument (duration 60 min) is excluded at the source
        //    by GetQuarterInstruments(). Any non-matching string — including
        //    the hour id — is rejected here.
        var quarters = _registry.GetQuarterInstruments();
        var matched = quarters.Any(id => string.Equals(
            id.ToString(), candidate.QuarterId, StringComparison.Ordinal));
        if (!matched)
        {
            return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                new BidMatrixValidationError("UnknownQuarter",
                    $"quarter_id '{candidate.QuarterId}' not in registered quarter instruments"));
        }

        // 3. TooManySteps: either side > MaxStepsPerSide.
        if (candidate.BuySteps.Length > _maxStepsPerSide)
        {
            return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                new BidMatrixValidationError("TooManySteps",
                    $"buy_steps count {candidate.BuySteps.Length} exceeds MaxStepsPerSide={_maxStepsPerSide}"));
        }
        if (candidate.SellSteps.Length > _maxStepsPerSide)
        {
            return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                new BidMatrixValidationError("TooManySteps",
                    $"sell_steps count {candidate.SellSteps.Length} exceeds MaxStepsPerSide={_maxStepsPerSide}"));
        }

        // 4. NonPositiveQuantity: any step's QuantityTicks <= 0.
        //    Note: PriceTicks is signed int64 — negative values are accepted
        //    per Nordic / CE market convention (renewable-surplus hours). Only
        //    quantity is strictly positive. Do NOT add a <= 0 check on PriceTicks.
        foreach (var s in candidate.BuySteps)
        {
            if (s.QuantityTicks <= 0)
            {
                return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                    new BidMatrixValidationError("NonPositiveQuantity",
                        $"buy_step quantity_ticks {s.QuantityTicks} must be > 0"));
            }
        }
        foreach (var s in candidate.SellSteps)
        {
            if (s.QuantityTicks <= 0)
            {
                return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                    new BidMatrixValidationError("NonPositiveQuantity",
                        $"sell_step quantity_ticks {s.QuantityTicks} must be > 0"));
            }
        }

        // 5. NonMonotonic: buy strict descending, sell strict ascending.
        //    Buyers bid merit order: willingness-to-pay decreases as step
        //    index increases. Sellers bid merit order: willingness-to-sell
        //    increases as step index increases. Strict (no duplicates).
        if (!IsStrictlyDescending(candidate.BuySteps))
        {
            return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                new BidMatrixValidationError("NonMonotonic",
                    "buy_steps must be strictly descending by price_ticks"));
        }
        if (!IsStrictlyAscending(candidate.SellSteps))
        {
            return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Fail(
                new BidMatrixValidationError("NonMonotonic",
                    "sell_steps must be strictly ascending by price_ticks"));
        }

        return ValidationResult<BidMatrixDto, BidMatrixValidationError>.Ok(candidate);
    }

    // Empty array or single-element array is trivially monotonic on both sides.
    private static bool IsStrictlyDescending(BidStepDto[] steps)
    {
        for (int i = 1; i < steps.Length; i++)
        {
            if (steps[i].PriceTicks >= steps[i - 1].PriceTicks) return false;
        }
        return true;
    }

    private static bool IsStrictlyAscending(BidStepDto[] steps)
    {
        for (int i = 1; i < steps.Length; i++)
        {
            if (steps[i].PriceTicks <= steps[i - 1].PriceTicks) return false;
        }
        return true;
    }
}
