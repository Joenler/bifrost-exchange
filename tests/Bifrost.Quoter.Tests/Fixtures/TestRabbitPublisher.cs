using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Exchange.Domain;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Pricing;
using Bifrost.Quoter.Schedule;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Quoter.Tests.Fixtures;

/// <summary>
/// Test double implementing both <see cref="IOrderContext"/> (the publisher
/// surface the quoter calls to submit / cancel / replace orders) and
/// <see cref="IRegimeChangePublisher"/> (the seam the quoter calls to emit
/// regime transitions). Each outbound envelope is captured into a thread-safe
/// list, and a rolling SHA-256 over the canonical capture stream is exposed
/// for byte-identical comparison across runs (the QTR-01 determinism gate).
///
/// CorrelationId construction here intentionally mirrors the production
/// QuoterCommandPublisher template (string of "quoter-{seq}-{area}-{startTicks}-{side}-{suffix}")
/// where {seq} is a deterministic monotonic sequence so two replays of the
/// same scenario produce identical correlation strings without depending on
/// any wall-clock source.
/// </summary>
public sealed class TestRabbitPublisher : IOrderContext, IRegimeChangePublisher
{
    /// <summary>One captured outbound envelope. <c>Kind</c> is one of
    /// "SubmitLimitOrder", "CancelOrder", "ReplaceOrder", "RegimeChange".
    /// <c>JsonBody</c> is the canonical serialized payload used for both the
    /// captured-list contents and the rolling SHA-256 hash input.</summary>
    public readonly record struct CapturedCommand(string Kind, string JsonBody);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<CapturedCommand> _captured = new();
    private readonly object _lock = new();
    private long _seq;

    /// <summary>Snapshot of every captured envelope in capture order.</summary>
    public IReadOnlyList<CapturedCommand> Captured
    {
        get
        {
            lock (_lock)
            {
                return _captured.ToArray();
            }
        }
    }

    /// <summary>
    /// SHA-256 over the canonical "Kind|JsonBody" join across the captured
    /// stream, hex-uppercase. Recomputed from the captured list on every read
    /// so the snapshot view is always self-consistent with the list contents.
    /// </summary>
    public string RollingSha256Hex
    {
        get
        {
            lock (_lock)
            {
                var joined = string.Join("\n", _captured.Select(c => $"{c.Kind}|{c.JsonBody}"));
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
                return Convert.ToHexString(hash);
            }
        }
    }

    public ILogger Logger => NullLogger.Instance;

    public CorrelationId SubmitLimitOrder(InstrumentId instrument, Side side, long priceTicks, decimal qty)
    {
        var seq = Interlocked.Increment(ref _seq);
        var area = instrument.DeliveryArea.Value;
        var startTicks = instrument.DeliveryPeriod.Start.UtcTicks;
        var corr = new CorrelationId($"test-{seq}-{area}-{startTicks}-{side}-submit");
        var payload = new
        {
            kind = "SubmitLimitOrder",
            area,
            startTicks,
            endTicks = instrument.DeliveryPeriod.End.UtcTicks,
            side = side.ToString(),
            priceTicks,
            qty,
            correlationId = corr.Value,
        };
        Capture("SubmitLimitOrder", JsonSerializer.Serialize(payload, JsonOptions));
        return corr;
    }

    public void CancelOrder(InstrumentId instrument, OrderId orderId)
    {
        var area = instrument.DeliveryArea.Value;
        var startTicks = instrument.DeliveryPeriod.Start.UtcTicks;
        var payload = new
        {
            kind = "CancelOrder",
            area,
            startTicks,
            endTicks = instrument.DeliveryPeriod.End.UtcTicks,
            orderId = orderId.Value,
        };
        Capture("CancelOrder", JsonSerializer.Serialize(payload, JsonOptions));
    }

    public void ReplaceOrder(InstrumentId instrument, OrderId orderId, long newPriceTicks, decimal? newQty)
    {
        var area = instrument.DeliveryArea.Value;
        var startTicks = instrument.DeliveryPeriod.Start.UtcTicks;
        var payload = new
        {
            kind = "ReplaceOrder",
            area,
            startTicks,
            endTicks = instrument.DeliveryPeriod.End.UtcTicks,
            orderId = orderId.Value,
            newPriceTicks,
            newQty,
        };
        Capture("ReplaceOrder", JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// The Quoter never calls <c>GetOrder</c> in its runtime flow (Plan 5
    /// Option B: tracker is the canonical source). Returning null is safe
    /// here for tests that may scan the surface; an explicit throw is
    /// intentionally avoided so a test that exercises an unrelated path
    /// does not accidentally hit a NotSupportedException.
    /// </summary>
    public Order? GetOrder(OrderId orderId) => null;

    public void Emit(RegimeTransition transition)
    {
        var payload = new
        {
            kind = "RegimeChange",
            from = transition.From.ToString(),
            to = transition.To.ToString(),
            mcForced = transition.McForced,
            reason = transition.Reason.ToString(),
        };
        Capture("RegimeChange", JsonSerializer.Serialize(payload, JsonOptions));
    }

    private void Capture(string kind, string body)
    {
        lock (_lock)
        {
            _captured.Add(new CapturedCommand(kind, body));
        }
    }
}
