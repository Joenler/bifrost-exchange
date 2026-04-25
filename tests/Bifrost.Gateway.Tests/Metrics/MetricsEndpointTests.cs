using Bifrost.Gateway.Tests.Fixtures;
using Xunit;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Tests.Metrics;

/// <summary>
/// GW-10 acceptance: <c>/metrics</c> endpoint serves the Prometheus exposition
/// format on the same Kestrel as gRPC, no auth (LAN-only posture per
/// PROJECT.md §Constraints + ARCHITECTURE.md §7).
///
/// The 10 metric families declared in <c>GatewayMetrics</c> match SPEC req 12
/// verbatim. Each test below either drives a production code-path that should
/// increment a specific counter or scrapes the endpoint and asserts the
/// expected family appears in the Prometheus text output.
///
/// prometheus-net's static <c>Prometheus.Metrics</c> registry is process-global,
/// so counters accumulate across tests within this class. Assertions therefore
/// use <c>≥</c> rather than equality and rely on <c>[Collection("Gateway")]</c>
/// + <c>DisableParallelization</c> from <see cref="GatewayCollection"/>.
/// </summary>
[Collection("Gateway")]
public sealed class MetricsEndpointTests
{
    private readonly GatewayTestHost _host;

    public MetricsEndpointTests(GatewayTestHost host) => _host = host;

    [Fact]
    public async Task Metrics_EndpointAccessible_NoAuth()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _host.CreateClient();

        // GET /metrics — prometheus-net.AspNetCore via app.MapMetrics() in Program.cs.
        var response = await http.GetAsync("/metrics", ct);

        Assert.True(response.IsSuccessStatusCode,
            $"GET /metrics should return success; got {(int)response.StatusCode}");
        var contentType = response.Content.Headers.ContentType?.MediaType;
        // prometheus-net serves either "text/plain" or the OpenMetrics negotiated form.
        Assert.NotNull(contentType);
        Assert.Contains("text/plain", contentType);
    }

    [Fact]
    public async Task Metrics_AllSpecRequiredFamilies_Exposed()
    {
        var ct = TestContext.Current.CancellationToken;
        // Drive a Register so the team-labelled families have at least one series.
        await DriveRegisterAsync("metrics-all-families", ct);

        using var http = _host.CreateClient();
        var body = await http.GetStringAsync("/metrics", ct);

        // SPEC req 12 surface — every family name must appear in the exposition.
        // The HELP/TYPE comments alone are sufficient for Counter/Gauge; Histogram
        // expands to <name>_bucket / <name>_sum / <name>_count.
        Assert.Contains("bifrost_gateway_orders_submitted_total", body);
        Assert.Contains("bifrost_gateway_orders_cancelled_total", body);
        Assert.Contains("bifrost_gateway_orders_replaced_total", body);
        Assert.Contains("bifrost_gateway_fills_total", body);
        Assert.Contains("bifrost_gateway_guard_rejects_total", body);
        Assert.Contains("bifrost_gateway_structural_rejects_total", body);
        Assert.Contains("bifrost_gateway_reconnects_total", body);
        Assert.Contains("bifrost_gateway_stream_latency_seconds", body);
        Assert.Contains("bifrost_gateway_ring_buffer_occupancy", body);
        Assert.Contains("bifrost_gateway_forecasts_dispatched_total", body);
    }

    [Fact]
    public async Task Metrics_AfterRegister_ReconnectsCounterIncremented()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "metrics-reconnect-team";
        var before = await ScrapeCounterAsync("bifrost_gateway_reconnects_total", team, ct);

        await DriveRegisterAsync(team, ct);

        var after = await ScrapeCounterAsync("bifrost_gateway_reconnects_total", team, ct);
        Assert.True(after >= before + 1,
            $"Reconnects should grow by ≥1 after Register; before={before} after={after}");
    }

    [Fact]
    public async Task Metrics_AfterStateGateReject_GuardRejectsLabeledStateGate()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "metrics-state-gate-team";
        // Drive RoundState into Gate so an OrderSubmit will be rejected by the
        // state-gate guard with REJECT_REASON_EXCHANGE_CLOSED → label "state_gate".
        _host.Round.SetState(Bifrost.Exchange.Application.RoundState.RoundState.Gate);
        try
        {
            using var channel = _host.CreateGrpcChannel();
            var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
            using var call = client.StreamStrategy(cancellationToken: ct);

            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
            }, ct);
            // Drain RegisterAck + 5 PositionSnapshots.
            for (var i = 0; i < 6; i++) Assert.True(await call.ResponseStream.MoveNext(ct));

            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                OrderSubmit = NewSubmit("H1", MarketProto.Side.Buy, priceTicks: 100, qtyTicks: 10),
            }, ct);
            Assert.True(await call.ResponseStream.MoveNext(ct));
            var resp = call.ResponseStream.Current;
            Assert.Equal(StrategyProto.MarketEvent.EventOneofCase.OrderReject, resp.EventCase);
            Assert.Equal(StrategyProto.RejectReason.ExchangeClosed, resp.OrderReject.Reason);

            await call.RequestStream.CompleteAsync();
        }
        finally
        {
            _host.Round.SetState(Bifrost.Exchange.Application.RoundState.RoundState.RoundOpen);
        }

        using var http = _host.CreateClient();
        var body = await http.GetStringAsync("/metrics", ct);
        // Look for the labelled series — order of labels in prometheus-net text format
        // is deterministic but not guaranteed alphabetical, so check both orderings.
        var labeled = $"bifrost_gateway_guard_rejects_total{{team_name=\"{team}\",guard=\"state_gate\"}}";
        var labeledAlt = $"bifrost_gateway_guard_rejects_total{{guard=\"state_gate\",team_name=\"{team}\"}}";
        Assert.True(
            body.Contains(labeled) || body.Contains(labeledAlt),
            $"expected guard_rejects_total{{team_name=\"{team}\",guard=\"state_gate\"}} in /metrics body");
    }

    [Fact]
    public async Task Metrics_AfterAcceptedSubmit_OrdersSubmittedIncremented()
    {
        var ct = TestContext.Current.CancellationToken;
        const string team = "metrics-submit-team";
        var before = await ScrapeCounterAsync("bifrost_gateway_orders_submitted_total", team, ct);

        using (var channel = _host.CreateGrpcChannel())
        {
            var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
            using var call = client.StreamStrategy(cancellationToken: ct);
            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                Register = new StrategyProto.Register { TeamName = team, LastSeenSequence = 0 },
            }, ct);
            for (var i = 0; i < 6; i++) Assert.True(await call.ResponseStream.MoveNext(ct));

            // RoundOpen by default (test host fixture initializer); MaxNotional default
            // is 50 MWh (qty_ticks 500_000); send a small qty so the guard chain accepts.
            await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
            {
                OrderSubmit = NewSubmit("H1", MarketProto.Side.Buy, priceTicks: 100, qtyTicks: 100),
            }, ct);
            await call.RequestStream.CompleteAsync();
            // Allow the writer task to flush metrics — drain any outbound frames.
            try { while (await call.ResponseStream.MoveNext(ct)) { } } catch { }
        }

        var after = await ScrapeCounterAsync("bifrost_gateway_orders_submitted_total", team, ct);
        Assert.True(after >= before + 1,
            $"OrdersSubmitted should grow by ≥1; before={before} after={after}");
    }

    private async Task DriveRegisterAsync(string teamName, CancellationToken ct)
    {
        using var channel = _host.CreateGrpcChannel();
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(channel);
        using var call = client.StreamStrategy(cancellationToken: ct);
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = teamName, LastSeenSequence = 0 },
        }, ct);
        for (var i = 0; i < 6; i++) Assert.True(await call.ResponseStream.MoveNext(ct));
        await call.RequestStream.CompleteAsync();
    }

    /// <summary>
    /// Scrape <c>/metrics</c> and parse the value of a single team-labelled
    /// counter. Returns 0 if the labelled series is absent (no increments yet).
    /// </summary>
    private async Task<double> ScrapeCounterAsync(string family, string teamName, CancellationToken ct)
    {
        using var http = _host.CreateClient();
        var body = await http.GetStringAsync("/metrics", ct);
        // Match either label ordering — prometheus-net text format renders labels
        // in declaration order, but be defensive.
        var prefixA = $"{family}{{team_name=\"{teamName}\"}}";
        var prefixB = $"{family}{{team_name=\"{teamName}\",";   // multi-label families
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(prefixA, StringComparison.Ordinal)
                || trimmed.StartsWith(prefixB, StringComparison.Ordinal))
            {
                var spaceIdx = trimmed.LastIndexOf(' ');
                if (spaceIdx <= 0) continue;
                if (double.TryParse(trimmed[(spaceIdx + 1)..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var v))
                {
                    return v;
                }
            }
        }
        return 0d;
    }

    private static StrategyProto.OrderSubmit NewSubmit(string instrumentId, MarketProto.Side side, long priceTicks, long qtyTicks) => new()
    {
        ClientId = string.Empty,
        Instrument = NewInstrument(instrumentId),
        Side = side,
        OrderType = MarketProto.OrderType.Limit,
        PriceTicks = priceTicks,
        QuantityTicks = qtyTicks,
    };

    private static MarketProto.Instrument NewInstrument(string id)
    {
        var hourStart = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (start, end) = id switch
        {
            "H1" => (hourStart, hourStart.AddHours(1)),
            "Q1" => (hourStart, hourStart.AddMinutes(15)),
            "Q2" => (hourStart.AddMinutes(15), hourStart.AddMinutes(30)),
            "Q3" => (hourStart.AddMinutes(30), hourStart.AddMinutes(45)),
            "Q4" => (hourStart.AddMinutes(45), hourStart.AddHours(1)),
            _ => (hourStart, hourStart.AddHours(1)),
        };
        return new MarketProto.Instrument
        {
            InstrumentId = id,
            DeliveryArea = "DE",
            DeliveryPeriodStartNs = start.ToUnixTimeMilliseconds() * 1_000_000L,
            DeliveryPeriodEndNs = end.ToUnixTimeMilliseconds() * 1_000_000L,
            ProductType = MarketProto.ProductType.Unspecified,
        };
    }
}
