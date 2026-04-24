namespace Bifrost.DahAuction.Validation;

/// <summary>
/// Typed rejection raised by <see cref="BidMatrixValidator"/>. <see cref="Code"/>
/// is one of the stable wire codes the HTTP endpoint returns to the team; the
/// caller serializes this record as <c>{ code, detail }</c> in the 400 body.
/// </summary>
/// <remarks>
/// Code vocabulary (6 validator codes; AuctionNotOpen is the 7th and is emitted
/// by the actor loop one layer deeper, NOT here):
///   - "Structural"           — malformed JSON / missing required fields
///                               (raised at the HTTP JSON binding layer; the
///                               validator itself receives a parsed DTO)
///   - "UnknownTeam"          — empty or whitespace-only TeamName
///   - "UnknownQuarter"       — QuarterId is not one of the 4 QH instrument ids
///   - "NonMonotonic"         — buy_steps not strictly descending OR sell_steps
///                               not strictly ascending by PriceTicks
///   - "TooManySteps"         — either side has &gt; MaxStepsPerSide entries
///   - "NonPositiveQuantity"  — any step's QuantityTicks &lt;= 0
/// </remarks>
public sealed record BidMatrixValidationError(string Code, string Detail);
