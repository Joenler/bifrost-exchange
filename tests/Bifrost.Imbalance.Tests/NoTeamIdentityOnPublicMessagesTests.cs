using System.Text.Json;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Imbalance.HostedServices;
using Bifrost.Imbalance.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Imbalance.Tests;

/// <summary>
/// SPEC Req 9 / IMB-07 invariant: Bifrost.Imbalance never publishes team
/// identity on a public topic. Two complementary gates:
/// <list type="bullet">
///   <item>Routing-key whitelist — every captured public emission must target
///   one of the allowed public.* prefixes. A regression that introduced a new
///   producer on a non-public or team-scoped routing key surfaces here.</item>
///   <item>Payload JSON scan — every captured public event serialized to JSON
///   must not contain the strings "team", "clientId", "teamId", "traderId",
///   "playerId", "userId". Catches a DTO field rename that accidentally adds
///   team identity to the wire, even if routing keys stayed clean.</item>
/// </list>
/// <para>
/// T-04-26 mitigation per plan threat model — this test is the CI fence. If a
/// future refactor adds a ClientId property to ImbalancePrintEvent or routes a
/// new public broadcast to a team-specific key, one of these facts turns red.
/// </para>
/// </summary>
public class NoTeamIdentityOnPublicMessagesTests
{
    private static readonly string[] AllowedPublicPrefixes =
    {
        "public.forecast",
        "public.forecast.revision",
        "public.imbalance.print.",
    };

    private static readonly string[] DisallowedPayloadSubstrings =
    {
        "team",
        "clientId",
        "teamId",
        "traderId",
        "playerId",
        "userId",
    };

    private static ImbalanceSimulatorOptions MakeOptions()
        => new ImbalanceSimulatorOptions
        {
            TForecastSeconds = 15,
            RoundDurationSeconds = 60,
            SigmaGateEuroMwh = 0.0,
            SigmaZeroEuroMwh = 0.0,
            TicksPerEuro = 100,
            K = 50.0,
            Alpha = 1.0,
            NScalingMwh = 100.0,
            GammaCalm = 1.0,
            DefaultRegime = "Calm",
            SReferenceTicksPerQuarter = new long[] { 50_000L, 52_000L, 54_000L, 53_000L },
            NonSettlementClientIds = new[] { "quoter", "dah-auction" },
        };

    /// <summary>
    /// Drive a full round-lifecycle so every public-emission path fires at
    /// least once, then assert every captured routing key matches an allowed
    /// public prefix. A producer that targeted a private or team-scoped key
    /// would surface as an unmatched routing key.
    /// </summary>
    [Fact]
    public async Task EnumerateAllProducers_OnlyKnownRoutingKeys()
    {
        var options = MakeOptions();
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
            var roundOpenTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionClosed, RoundState.RoundOpen, roundOpenTs));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            await host.InjectAsync(new FillMessage(
                TsNs: 10L, ClientId: "alpha",
                InstrumentId: "DE.999901010000-999901010015",
                QuarterIndex: 0, Side: "Buy", QuantityTicks: 500L));
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);

            var gateTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.RoundOpen, RoundState.Gate, gateTs));
            await Task.Delay(100, TestContext.Current.CancellationToken);

            await host.InjectAsync(new RoundStateMessage(
                RoundState.Gate, RoundState.Settled, gateTs + 1_000_000_000L));
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }

        // Sanity: we actually captured something — a vacuous Assert.All would
        // trivially pass on an empty collection.
        Assert.NotEmpty(host.Publisher.CapturedPublic);

        // Every captured public routing key matches an allowed prefix. The
        // whitelist is deliberately narrow: adding a new prefix here requires
        // a deliberate update, forcing a review of the new producer.
        Assert.All(host.Publisher.CapturedPublic, captured =>
            Assert.True(
                AllowedPublicPrefixes.Any(prefix =>
                    captured.RoutingKey.StartsWith(prefix, StringComparison.Ordinal)),
                $"Disallowed public routing key: {captured.RoutingKey}"));

        // Zero team identity tokens in any routing key. Real client ids from
        // the round ("alpha", "quoter") must not appear on the public bus
        // regardless of routing-key shape.
        foreach (var captured in host.Publisher.CapturedPublic)
        {
            Assert.DoesNotContain("alpha", captured.RoutingKey, StringComparison.Ordinal);
            Assert.DoesNotContain("quoter", captured.RoutingKey, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Serialize every captured public-event payload to JSON and assert none
    /// contain the disallowed substrings. This is the payload-layer mirror of
    /// the routing-key whitelist — a DTO rename that added a ClientId field to
    /// ForecastUpdateEvent or ImbalancePrintEvent would surface here even if
    /// routing keys stayed clean.
    /// </summary>
    [Fact]
    public async Task ProtoPayloads_ContainNoTeamFields()
    {
        var options = MakeOptions();
        await using var host = new TestImbalanceHost(RoundState.IterationOpen, options);
        var timer = new ForecastTimerHostedService(
            host.Time, host.Channel, host.Clock,
            Options.Create(options),
            NullLogger<ForecastTimerHostedService>.Instance);
        await timer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await host.InjectAsync(new RoundStateMessage(
                RoundState.IterationOpen, RoundState.AuctionOpen, 0L));
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionOpen, RoundState.AuctionClosed, 1L));
            var roundOpenTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.AuctionClosed, RoundState.RoundOpen, roundOpenTs));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            await host.InjectAsync(new FillMessage(
                TsNs: 10L, ClientId: "alpha",
                InstrumentId: "DE.999901010000-999901010015",
                QuarterIndex: 0, Side: "Buy", QuantityTicks: 500L));
            await host.AdvanceSecondsAsync(options.TForecastSeconds * 2);

            var gateTs = host.Clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            await host.InjectAsync(new RoundStateMessage(
                RoundState.RoundOpen, RoundState.Gate, gateTs));
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        finally
        {
            await timer.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(host.Publisher.CapturedPublic);

        foreach (var captured in host.Publisher.CapturedPublic)
        {
            var json = JsonSerializer.Serialize(captured.Evt, captured.Evt.GetType());
            foreach (var banned in DisallowedPayloadSubstrings)
            {
                Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Reflection-level mirror of the JSON scan. Guarantees no property named
    /// ClientId / Team / TraderId exists on any public-scoped DTO regardless
    /// of whether JsonSerializer would emit it (e.g. [JsonIgnore] on a field
    /// would hide it from the JSON scan but still leaks it on Protobuf). Only
    /// declared, public, instance properties — inherited object members like
    /// ToString are excluded.
    /// </summary>
    [Fact]
    public void PublicEventTypes_HaveNoTeamIdentityProperties()
    {
        var publicEventTypes = new[]
        {
            typeof(ForecastUpdateEvent),
            typeof(ForecastRevisionEvent),
            typeof(ImbalancePrintEvent),
        };

        foreach (var t in publicEventTypes)
        {
            var propNames = t.GetProperties(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(p => p.Name.ToLowerInvariant())
                .ToArray();

            Assert.DoesNotContain("clientid", propNames);
            Assert.DoesNotContain("team", propNames);
            Assert.DoesNotContain("teamid", propNames);
            Assert.DoesNotContain("traderid", propNames);
            Assert.DoesNotContain("playerid", propNames);
            Assert.DoesNotContain("userid", propNames);
        }
    }
}
