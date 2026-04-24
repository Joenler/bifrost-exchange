using Xunit;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// ImbalanceSettlementEvent is internal-only — no gRPC counterpart exists.
/// Teams consume it via their private RabbitMQ queue
/// (private.imbalance.settlement.&lt;clientId&gt;); it is intentionally absent
/// from the external wire per D-14 (ADR-0003 + SPEC req 6 locate this row
/// on the private per-team channel, never on the team-facing gRPC stream).
///
/// The skip placeholder keeps a row in this test suite so the gateway
/// mapping documentation has a cross-referenceable "no proto analog" test
/// target — if a future change adds a proto for it, this skipped fact is
/// the canonical place to wire the round-trip.
/// </summary>
public sealed class ImbalanceSettlementTranslationTests
{
    [Fact(Skip = "ImbalanceSettlementEvent has no proto counterpart — internal-only per D-14.")]
    public void ImbalanceSettlement_HasNoProtoAnalog()
    {
        // Intentionally empty — skip reason documents the contract.
    }
}
