namespace Bifrost.DahAuction.Commands;

/// <summary>
/// Post-actor-loop result for a <see cref="SubmitBidCommand"/>. On Accepted
/// the HTTP endpoint returns 200; on !Accepted the endpoint returns 400 with
/// the <see cref="RejectCode"/> + <see cref="RejectDetail"/> in the JSON body.
/// </summary>
public sealed record SubmitBidResult(bool Accepted, string? RejectCode, string? RejectDetail);
