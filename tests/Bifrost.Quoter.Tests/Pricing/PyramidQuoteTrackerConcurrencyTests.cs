using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Pricing;
using Xunit;
// Disambiguate quoter-side OrderAccepted (correlation-id-bearing reconciliation event)
// from matching-engine-internal Bifrost.Exchange.Domain.OrderAccepted.
using OrderAccepted = Bifrost.Quoter.Pricing.Events.OrderAccepted;

namespace Bifrost.Quoter.Tests.Pricing;

/// <summary>
/// Concurrency stress tests for PyramidQuoteTracker. See UPSTREAM.md for Arena
/// provenance and the assertion-style adaptation (FluentAssertions -> plain xUnit
/// Assert). InstrumentId construction adapted from Arena's 4-tuple shape to
/// BIFROST's (DeliveryArea, DeliveryPeriod) record-struct.
/// </summary>
public sealed class PyramidQuoteTrackerConcurrencyTests
{
    private static readonly DeliveryArea TestArea = new("DE1");

    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 6, 10, 0, 0, TimeSpan.Zero);

    private static InstrumentId MakeInstrument(int hourOffset) => new(
        TestArea,
        new DeliveryPeriod(
            BaseTime.AddHours(hourOffset),
            BaseTime.AddHours(hourOffset + 1)));

    [Fact]
    public async Task Concurrent_GetSlotSummary_and_TrackOrder_produce_no_exceptions()
    {
        var tracker = new PyramidQuoteTracker(3, TimeProvider.System);
        var instruments = Enumerable.Range(0, 20)
            .Select(MakeInstrument)
            .ToArray();

        foreach (var inst in instruments)
            tracker.GetOrCreate(inst);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        var readErrors = 0;
        var writeErrors = 0;
        var readIterations = 0L;

        var readerTask = Task.Run(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (working, pending, empty) = tracker.GetSlotSummary();
                    Assert.True(working + pending + empty >= 0);
                    Interlocked.Increment(ref readIterations);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                Interlocked.Increment(ref readErrors);
            }
        }, ct);

        var writerTask = Task.Run(() =>
        {
            var rng = new Random(42);
            var corrCounter = 0L;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var inst = instruments[rng.Next(instruments.Length)];
                    var side = rng.Next(2) == 0 ? Side.Buy : Side.Sell;
                    var level = rng.Next(3);
                    var corrId = new CorrelationId($"corr-{corrCounter++}");

                    tracker.TrackOrder(inst, side, level, corrId);

                    if (corrCounter % 3 == 0)
                    {
                        var orderId = new OrderId(corrCounter);
                        tracker.OnOrderAccepted(new OrderAccepted(
                            orderId, inst, side, OrderType.Limit, 100, 1.0m, null, corrId));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                Interlocked.Increment(ref writeErrors);
            }
        }, ct);

        await Task.WhenAll(readerTask, writerTask);

        Assert.Equal(0, readErrors);
        Assert.Equal(0, writeErrors);
        Assert.True(readIterations > 0);
    }

    [Fact]
    public async Task Concurrent_ClearStalePending_and_TrackOrder_produce_no_exceptions()
    {
        var tracker = new PyramidQuoteTracker(3, TimeProvider.System);
        var instruments = Enumerable.Range(0, 10)
            .Select(MakeInstrument)
            .ToArray();

        foreach (var inst in instruments)
            tracker.GetOrCreate(inst);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = cts.Token;
        var clearErrors = 0;
        var trackErrors = 0;

        var clearTask = Task.Run(() =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    tracker.ClearStalePending(TimeSpan.FromMilliseconds(1));
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                Interlocked.Increment(ref clearErrors);
            }
        }, ct);

        var trackTask = Task.Run(() =>
        {
            var rng = new Random(99);
            var counter = 0L;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var inst = instruments[rng.Next(instruments.Length)];
                    var side = rng.Next(2) == 0 ? Side.Buy : Side.Sell;
                    var level = rng.Next(3);
                    tracker.TrackOrder(inst, side, level, new CorrelationId($"track-{counter++}"));
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                Interlocked.Increment(ref trackErrors);
            }
        }, ct);

        await Task.WhenAll(clearTask, trackTask);

        Assert.Equal(0, clearErrors);
        Assert.Equal(0, trackErrors);
    }
}
