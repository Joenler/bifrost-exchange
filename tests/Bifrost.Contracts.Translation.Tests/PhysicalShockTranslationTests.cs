using Google.Protobuf;
using Xunit;
using EventsProto = Bifrost.Contracts.Events;

namespace Bifrost.Contracts.Translation.Tests;

/// <summary>
/// bifrost.events.v1.PhysicalShock ↔ Bifrost.Contracts.Internal.Events.PhysicalShockEvent.
///
/// PhysicalShock carries (mw, label, persistence enum, optional int32
/// quarter_index — v1.1.0). DTO adds TimestampNs (from enclosing Event
/// envelope) and lifts QuarterIndex to non-nullable int (orchestrator
/// enforces required).
///
/// The assertion explicitly verifies HasQuarterIndex on the round-trip
/// message so the proto3-optional wire signal survives — field presence,
/// not just value equality.
/// </summary>
public sealed class PhysicalShockTranslationTests
{
    [Fact]
    public void PhysicalShock_RoundTrips_ViaDto_PreservingQuarterIndex()
    {
        var original = new EventsProto.PhysicalShock
        {
            Mw = -500,
            Label = "Gen-Trip-B1",
            Persistence = EventsProto.ShockPersistence.Round,
            QuarterIndex = 2,
        };
        var originalBytes = original.ToByteArray();

        var dto = TranslationFixtures.ToInternal(original, timestampNs: 1_745_400_000_000_000_152L);

        var roundtrip = TranslationFixtures.ToProto(dto);
        Assert.True(roundtrip.HasQuarterIndex);
        var roundtripBytes = roundtrip.ToByteArray();

        Assert.Equal(originalBytes, roundtripBytes);
    }
}
