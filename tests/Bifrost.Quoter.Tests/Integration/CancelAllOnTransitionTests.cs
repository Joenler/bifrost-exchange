using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Schedule;
using Bifrost.Quoter.Tests.Fixtures;
using Xunit;
// Disambiguate against Bifrost.Exchange.Domain.OrderAccepted (different shape;
// the tracker consumes the quoter-side reconciliation event with CorrelationId).
using OrderAccepted = Bifrost.Quoter.Pricing.Events.OrderAccepted;

namespace Bifrost.Quoter.Tests.Integration;

/// <summary>
/// QTR-05 integration tests. Every regime transition (scheduled, Markov, or
/// MC-forced) must:
///   1. Emit Event.RegimeChange FIRST (step 2 of the protocol).
///   2. Cancel all resting quoter orders across all 5 instruments BEFORE any
///      new SubmitLimitOrder for the new regime's pyramid (step 3).
/// Both invariants are checked against the captured TestRabbitPublisher
/// stream. Pre-seeded resting orders simulate a quoter that has been
/// running long enough to fully populate its 3-level x 2-side x 5-instrument
/// pyramid (30 working orders) before the transition fires.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class CancelAllOnTransitionTests
{
    [Fact]
    public async Task CancelAllOnTransition_EmitsRegimeChangeBeforeAnyCancel_AndBeforeNewPyramidSubmit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost("calm-drift.json", overrideSeed: 314);
        await host.Quoter.StartAsync(ct);
        // Let the quoter populate its pyramid naturally over a few ticks so
        // the tracker has working orders to cancel when the transition fires.
        await host.AdvanceSecondsAsync(2.0);

        // Inject an additional "synthetic" 30 tracked orders directly into
        // the tracker (3 levels x 2 sides x 5 instruments) so the cancel
        // burst has a guaranteed minimum count regardless of how many real
        // tick-driven submits the quoter already issued.
        SeedTracker(host);

        // Snapshot the captured stream just before the transition.
        var preTransitionCount = host.TestPublisher.Captured.Count;

        // Trigger an MC-force transition.
        var nonce = Guid.NewGuid();
        await host.Inbox.Writer.WriteAsync(new RegimeForceMessage(Regime.Volatile, nonce), ct);
        await host.AdvanceSecondsAsync(1.0);
        await host.Quoter.StopAsync(ct);

        // Inspect the post-transition slice of the capture stream.
        var slice = host.TestPublisher.Captured.Skip(preTransitionCount).ToList();

        var regimeIdx = slice.FindIndex(c => c.Kind == "RegimeChange");
        var firstCancelIdx = slice.FindIndex(c => c.Kind == "CancelOrder");

        Assert.True(regimeIdx >= 0, "expected at least one RegimeChange after the MC force");
        Assert.True(firstCancelIdx >= 0, "expected at least one CancelOrder after the transition");
        Assert.True(regimeIdx < firstCancelIdx,
            $"RegimeChange (idx {regimeIdx}) must precede first CancelOrder (idx {firstCancelIdx})");

        // Cancel count must be ≥30 (pre-seeded) + however many real orders
        // the quoter had already worked from the warmup ticks.
        var cancelCount = slice.Count(c => c.Kind == "CancelOrder");
        Assert.True(cancelCount >= 30, $"expected ≥30 cancels after transition, got {cancelCount}");

        // Any new SubmitLimitOrder envelope after the transition must come
        // AFTER all cancels in the slice (cancel-burst-then-requote ordering).
        var firstSubmitAfterTransition = slice.FindIndex(regimeIdx + 1, c => c.Kind == "SubmitLimitOrder");
        if (firstSubmitAfterTransition >= 0)
        {
            var lastCancelIdx = slice.FindLastIndex(c => c.Kind == "CancelOrder");
            Assert.True(lastCancelIdx < firstSubmitAfterTransition,
                $"all cancels (last at {lastCancelIdx}) must precede any new SubmitLimitOrder (first at {firstSubmitAfterTransition})");
        }
    }

    [Fact]
    public async Task CancelAllOnTransition_UsesAllFiveInstrumentsInIndexOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = new TestQuoterHost("calm-drift.json", overrideSeed: 271);
        await host.Quoter.StartAsync(ct);
        await host.AdvanceSecondsAsync(2.0);

        SeedTracker(host);

        var preTransitionCount = host.TestPublisher.Captured.Count;

        var nonce = Guid.NewGuid();
        await host.Inbox.Writer.WriteAsync(new RegimeForceMessage(Regime.Volatile, nonce), ct);
        await host.AdvanceSecondsAsync(1.0);
        await host.Quoter.StopAsync(ct);

        // The expected sorted instrument order matches the Quoter's internal
        // _sortedInstruments (DeliveryArea ordinal then DeliveryPeriod.Start).
        var expectedOrder = host.Instruments
            .OrderBy(i => i.DeliveryArea.Value, StringComparer.Ordinal)
            .ThenBy(i => i.DeliveryPeriod.Start)
            .ToArray();

        // Extract the Cancel envelopes after the transition; dedupe by the
        // full (area, startTicks, endTicks) triple because two BIFROST
        // instruments share startTicks (the hour and Q1 both start at the
        // same hourStart -- the period END distinguishes them).
        var slice = host.TestPublisher.Captured.Skip(preTransitionCount).ToList();
        var cancelOrder = new List<(string Area, long StartTicks, long EndTicks)>();
        var seen = new HashSet<(string, long, long)>();
        foreach (var cmd in slice.Where(c => c.Kind == "CancelOrder"))
        {
            var (area, startTicks, endTicks) = ExtractInstrumentMarkers(cmd.JsonBody);
            if (seen.Add((area, startTicks, endTicks)))
                cancelOrder.Add((area, startTicks, endTicks));
        }

        // Every one of the 5 instruments must show up in the cancel stream.
        Assert.Equal(expectedOrder.Length, cancelOrder.Count);

        // First-appearance order must match the deterministic sorted order.
        for (var i = 0; i < expectedOrder.Length; i++)
        {
            var expected = expectedOrder[i];
            Assert.Equal(expected.DeliveryArea.Value, cancelOrder[i].Area);
            Assert.Equal(expected.DeliveryPeriod.Start.UtcTicks, cancelOrder[i].StartTicks);
            Assert.Equal(expected.DeliveryPeriod.End.UtcTicks, cancelOrder[i].EndTicks);
        }
    }

    private static void SeedTracker(TestQuoterHost host)
    {
        // 3 levels x 2 sides x 5 instruments = 30 synthetic accepted orders.
        // Each gets a unique tracked CorrelationId and a unique OrderId so
        // the tracker's cancel-all loop will issue 30 cancels.
        long fakeOrderIdSeed = 100_000;
        foreach (var inst in host.Instruments)
        {
            foreach (var side in new[] { Side.Buy, Side.Sell })
            {
                for (var level = 0; level < 3; level++)
                {
                    var corr = new CorrelationId($"seed-{inst.DeliveryArea.Value}-{inst.DeliveryPeriod.Start.UtcTicks}-{side}-{level}");
                    host.Tracker.TrackOrder(inst, side, level, corr, priceTicks: 5000L);
                    var accepted = new OrderAccepted(
                        OrderId: new OrderId(fakeOrderIdSeed++),
                        Instrument: inst,
                        Side: side,
                        OrderType: OrderType.Limit,
                        PriceTicks: 5000L,
                        Quantity: 1m,
                        DisplaySliceSize: null,
                        CorrelationId: corr,
                        ExchangeTimestampNs: 0L);
                    host.Tracker.OnOrderAccepted(accepted);
                }
            }
        }
    }

    private static (string Area, long StartTicks, long EndTicks) ExtractInstrumentMarkers(string jsonBody)
    {
        // The TestRabbitPublisher payload shape uses camelCase keys:
        //   {"kind":"CancelOrder","area":"DE","startTicks":<long>,"endTicks":<long>,"orderId":<long>}
        var areaKey = "\"area\":\"";
        var startKey = "\"startTicks\":";
        var endKey = "\"endTicks\":";
        var areaStart = jsonBody.IndexOf(areaKey, StringComparison.Ordinal) + areaKey.Length;
        var areaEnd = jsonBody.IndexOf('"', areaStart);
        var area = jsonBody[areaStart..areaEnd];
        var startNumStart = jsonBody.IndexOf(startKey, StringComparison.Ordinal) + startKey.Length;
        var startNumEnd = jsonBody.IndexOf(',', startNumStart);
        var startTicks = long.Parse(jsonBody[startNumStart..startNumEnd], System.Globalization.CultureInfo.InvariantCulture);
        var endNumStart = jsonBody.IndexOf(endKey, StringComparison.Ordinal) + endKey.Length;
        var endNumEnd = jsonBody.IndexOf(',', endNumStart);
        var endTicks = long.Parse(jsonBody[endNumStart..endNumEnd], System.Globalization.CultureInfo.InvariantCulture);
        return (area, startTicks, endTicks);
    }
}
