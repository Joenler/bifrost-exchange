using System.Text.Json;
using Bifrost.Contracts.Internal.Events;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// IMB-07 / SPEC req 9 payload-shape invariants for the public forecast DTO.
/// The public forecast carries NO per-team identity — the cohort-jittered
/// per-team dispatch is Phase 07 Gateway's job. Any field named team / clientId
/// / confidence / variance would break the public fairness invariant.
/// <para>
/// Two layers of assertion:
/// </para>
/// <list type="number">
/// <item>Reflection over <see cref="ForecastUpdateEvent"/>'s public properties —
/// catches a regression where a new field is added to the record.</item>
/// <item>Serialized-JSON substring scan — catches a regression where a field
/// is renamed at the wire layer via a <c>[JsonPropertyName]</c> attribute but
/// the property name is kept innocuous.</item>
/// </list>
/// </summary>
public class ForecastPayloadShapeTests
{
    [Fact]
    public void ForecastUpdateEvent_HasNoTeamIdentityFields()
    {
        var propNames = typeof(ForecastUpdateEvent).GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToArray();

        Assert.DoesNotContain("team", propNames);
        Assert.DoesNotContain("teamid", propNames);
        Assert.DoesNotContain("clientid", propNames);
        Assert.DoesNotContain("confidence", propNames);
        Assert.DoesNotContain("variance", propNames);

        // Positive: the three expected fields are present (defensive assertion
        // — if a future refactor renames the record, this catches it and forces
        // the rename to be intentional).
        Assert.Contains("forecastpriceticks", propNames);
        Assert.Contains("horizonns", propNames);
        Assert.Contains("timestampns", propNames);
    }

    [Fact]
    public void ForecastUpdateEvent_SerializedPayload_HasOnlyExpectedFieldNames()
    {
        var evt = new ForecastUpdateEvent(
            ForecastPriceTicks: 51_000L,
            HorizonNs: 60_000_000_000L,
            TimestampNs: 1_745_400_000_000_000_100L);

        var json = JsonSerializer.Serialize(evt);

        // Positive: the three expected fields are present. Test stays agnostic
        // of camelCase vs PascalCase by accepting either.
        Assert.Contains("orecastPriceTicks", json);
        Assert.Contains("orizonNs", json);
        Assert.Contains("imestampNs", json);

        // Negative: no team-identity strings at the wire layer.
        Assert.DoesNotContain("team", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confidence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("variance", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForecastUpdateEvent_ExactlyThreePublicProperties()
    {
        // A regression that adds a 4th property to the record would surface on
        // this assertion, forcing the change to be intentional. The three
        // properties are: ForecastPriceTicks, HorizonNs, TimestampNs.
        var count = typeof(ForecastUpdateEvent).GetProperties().Length;
        Assert.Equal(3, count);
    }
}
