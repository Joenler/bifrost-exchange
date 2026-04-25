using System.Diagnostics;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Bifrost.Gateway.Load.Tests;

/// <summary>
/// Shape of <c>load-report.json</c> emitted by the harness. Property names are
/// serialized via <c>JsonNamingPolicy.SnakeCaseLower</c> in
/// <see cref="EightTeamLoadTest"/> so the on-disk JSON keys are
/// <c>p99_inbound_ms</c> etc. — exactly what
/// <c>.github/workflows/ci-gateway-load.yml</c>'s jq filter expects.
/// </summary>
public sealed record LoadReport(
    double P50InboundMs,
    double P99InboundMs,
    double P50FanoutMs,
    double P99FanoutMs,
    long MsgCount,
    double DurationS);

/// <summary>
/// 8-team load harness. Stands up an in-process gateway via
/// <see cref="WebApplicationFactory{Program}"/>, points it at a
/// real <see cref="RabbitMqContainerFixture"/>, then spins up
/// <c>teamCount</c> synthetic <see cref="SyntheticTeamClient"/>
/// instances (each over its own <see cref="GrpcChannel"/>) and runs
/// them concurrently for the configured duration.
///
/// SPEC req 11 SLO budget (GW-09):
///   p99 inbound  &lt; 50 ms  (client-send → gateway-ack)
///   p99 fan-out &lt; 100 ms  (RabbitMQ-deliver → wire-emit)
///
/// **Outbound fan-out instrumentation deferred to Phase 12a.** See the
/// <see cref="MeasureForecastFanoutP99Async"/> XML doc for the timing-contract
/// pinning (Pitfall 4 — the timer MUST start at dispatcher-decides-emit, NOT
/// at RabbitMQ-delivers-the-forecast).
/// </summary>
public sealed class LoadHarness : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RabbitMqContainerFixture _rabbit;

    private LoadHarness(WebApplicationFactory<Program> factory, RabbitMqContainerFixture rabbit)
    {
        _factory = factory;
        _rabbit = rabbit;
    }

    public static Task<LoadHarness> CreateAsync(RabbitMqContainerFixture rabbit)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Point the in-process gateway at the Testcontainers RabbitMQ.
                ["RabbitMq:Host"] = rabbit.Hostname,
                ["RabbitMq:Port"] = rabbit.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RabbitMq:Username"] = "guest",
                ["RabbitMq:Password"] = "guest",
                // Use an absent path so GuardThresholds.LoadFromFile falls back to
                // ADR-0004 defaults (matches Bifrost.Gateway.Tests.Fixtures.GatewayTestHost).
                ["Gateway:Guards:ConfigPath"] = Path.Combine(
                    Path.GetTempPath(),
                    $"bifrost-gateway-load-{Guid.NewGuid():N}.absent.json"),
                ["RoundState:Initial"] = "RoundOpen",
            }));
        });

        // Force lazy host construction so the in-process gateway is up before
        // synthetic teams connect.
        _ = factory.CreateClient();

        return Task.FromResult(new LoadHarness(factory, rabbit));
    }

    public async Task<LoadReport> RunAsync(
        int teamCount,
        int ordersPerSecondPerTeam,
        TimeSpan duration,
        CancellationToken ct)
    {
        var clients = new SyntheticTeamClient[teamCount];
        var channels = new GrpcChannel[teamCount];
        var sw = Stopwatch.StartNew();

        try
        {
            for (var i = 0; i < teamCount; i++)
            {
                var http = _factory.CreateClient();
                channels[i] = GrpcChannel.ForAddress(
                    http.BaseAddress!,
                    new GrpcChannelOptions { HttpClient = http });

                clients[i] = new SyntheticTeamClient(
                    teamName: $"team-{i:D2}",
                    channel: channels[i],
                    seed: 42 + i,
                    targetRatePerSecond: ordersPerSecondPerTeam);
            }

            await Task.WhenAll(clients.Select(c => c.RunAsync(duration, ct)));
        }
        finally
        {
            foreach (var c in channels)
            {
                c.Dispose();
            }
        }

        sw.Stop();

        var allInbound = clients.SelectMany(c => c.InboundLatencyMs).ToArray();
        Array.Sort(allInbound);
        var p50In = Percentile(allInbound, 0.50);
        var p99In = Percentile(allInbound, 0.99);

        var (p50Out, p99Out) = await MeasureForecastFanoutP99Async(ct);

        return new LoadReport(
            P50InboundMs: p50In,
            P99InboundMs: p99In,
            P50FanoutMs: p50Out,
            P99FanoutMs: p99Out,
            MsgCount: allInbound.LongLength,
            DurationS: sw.Elapsed.TotalSeconds);
    }

    private static double Percentile(double[] sortedAsc, double q)
    {
        if (sortedAsc.Length == 0)
        {
            return 0;
        }

        var idx = Math.Min(sortedAsc.Length - 1, (int)Math.Ceiling(sortedAsc.Length * q) - 1);
        if (idx < 0)
        {
            idx = 0;
        }

        return sortedAsc[idx];
    }

    /// <summary>
    /// Outbound fan-out timing. **v1 placeholder — returns (0, 0).**
    ///
    /// Pitfall 4 (07-RESEARCH.md lines 538-548): when this placeholder is
    /// replaced with real fan-out instrumentation in Phase 12a, the timer
    /// MUST start at the moment the gateway's <c>ForecastDispatcher</c>
    /// decides to emit to a team — NOT at the moment RabbitMQ delivers
    /// the <c>public.forecast</c> envelope. The 100 ms p99 fan-out SLO is
    /// measured against the dispatcher-decides clock, not the
    /// RabbitMQ-delivers clock. Reading the SLO against the latter would
    /// have it always violated by the cohort interval (15 s) + jitter
    /// (700 ms), which is a misreading of the timing contract.
    ///
    /// The CI gate
    /// (<c>.github/workflows/ci-gateway-load.yml</c>) tolerates
    /// <c>p99_fanout_ms == 0</c> as "not yet instrumented" — that
    /// tolerance is re-tightened in Phase 12a once the
    /// <c>ForecastDispatcher</c> records per-emit timestamps.
    /// </summary>
    private static Task<(double P50, double P99)> MeasureForecastFanoutP99Async(CancellationToken ct)
    {
        _ = ct;
        return Task.FromResult<(double, double)>((0.0, 0.0));
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
