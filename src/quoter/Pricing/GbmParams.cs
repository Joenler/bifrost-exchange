namespace Bifrost.Quoter.Pricing;

/// <summary>
/// Per-tick GBM parameters injected into <see cref="GbmPriceModel.StepAll"/>
/// by the caller's regime schedule. Replaces Arena's internal regime FSM
/// (<c>RegimeState</c> / <c>RegimeType</c> / <c>TryTransition</c>) with an
/// externally-owned (drift, vol) pair so the regime schedule remains the
/// single source of regime truth.
/// </summary>
public sealed record GbmParams(double Drift, double Vol);
