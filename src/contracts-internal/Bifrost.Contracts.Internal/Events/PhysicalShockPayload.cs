namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Wire DTO for orchestrator-emitted publications on
/// <c>bifrost.events.v1/events.physical_shock</c>. The orchestrator emits this
/// envelope from <c>NewsFireCmd</c> when the resolved canned-library entry
/// carries a shock payload (per ADR-0005 canned-library format).
/// </summary>
/// <remarks>
/// Sibling DTO <see cref="PhysicalShockEvent"/> already exists for the
/// distinct path where the imbalance simulator consumes operator-injected
/// per-quarter shocks (<c>PhysicalShockCmd</c>) — that DTO carries
/// <c>QuarterIndex</c> as a required <see cref="int"/> plus a
/// <c>TimestampNs</c> for the simulator's accumulator. This DTO covers the
/// orchestrator's news-library-originated path, where:
///
///   * the news library has no notion of a target quarter
///     (<see cref="QuarterIndex"/> is therefore nullable; null = "no quarter
///     hint, broadcast for downstream interpretation"),
///   * the timestamp is supplied by the standard envelope
///     <c>TimestampUtc</c> field rather than a duplicate payload field.
///
/// Both DTOs share the underlying physical concept; keeping them as separate
/// records — instead of force-fitting one into the other — preserves the
/// imbalance simulator's defense-in-depth invariant that operator-injected
/// shocks always carry a valid quarter index, while leaving the
/// news-library-driven publish path free of a synthetic placeholder
/// <c>QuarterIndex=-1</c>. A future cross-phase consolidation may unify the
/// two; until then, downstream consumers select the DTO that matches their
/// subscription.
/// </remarks>
public sealed record PhysicalShockPayload(
    int Mw,
    string Label,
    string Persistence,
    int? QuarterIndex = null);
