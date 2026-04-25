using System.Diagnostics;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

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

    public static async Task<LoadHarness> CreateAsync(RabbitMqContainerFixture rabbit)
    {
        // Bootstrap the central-machine RabbitMQ topology against the bare
        // Testcontainers broker BEFORE the gateway starts. In production these
        // exchanges are declared by the matching engine (bifrost.cmd, via
        // RabbitMqTopology.DeclareExchangeTopologyAsync — see
        // src/exchange/Exchange.Infrastructure.RabbitMq/RabbitMqTopology.cs)
        // and by the orchestrator (bifrost.round.v1) and dah-auction
        // (bifrost.auction). The gateway PUBLISHES to bifrost.cmd via its
        // GatewayCommandPublisher; if the exchange does not exist the
        // publisher's IChannel is closed by the broker with a 404 NOT_FOUND
        // (channel.exception class=60 method=40 — basic.publish), which then
        // surfaces as AlreadyClosedException on every subsequent OrderSubmit
        // and tears down the bidi stream. The other exchanges are declared
        // ad-hoc by the consumer-side BackgroundServices (PrivateEventConsumer
        // etc.) when they bind, so we only need to seed the publish-side
        // exchange here.
        await DeclareCentralExchangesAsync(rabbit);

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
        // synthetic teams connect. Reading `factory.Server` triggers the same
        // EnsureServer() path as CreateClient() but without the WAF
        // CreateDefaultClient pipeline (RedirectHandler etc.) — see RunAsync
        // below for why bidi gRPC must NOT go through that pipeline.
        _ = factory.Server;

        return new LoadHarness(factory, rabbit);
    }

    private static async Task DeclareCentralExchangesAsync(RabbitMqContainerFixture rabbit)
    {
        var factory = new ConnectionFactory
        {
            HostName = rabbit.Hostname,
            Port = rabbit.Port,
            UserName = "guest",
            Password = "guest",
        };
        await using var connection = await factory.CreateConnectionAsync("bifrost-load-bootstrap");
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareExchangeTopologyAsync(channel);
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
            // gRPC bidi over WebApplicationFactory MUST go through
            // TestServer.CreateHandler — NOT factory.CreateClient. The default
            // WAF HttpClient injects Microsoft.AspNetCore.Mvc.Testing.Handlers
            // .RedirectHandler, which calls HttpContent.LoadIntoBufferAsyncCore
            // on the request body so it can be replayed on a 30x. gRPC bidi
            // streams use PushStreamContent whose body never ends until the
            // call completes — buffering it deadlocks the request before it
            // is dispatched to the test server (no RegisterAck ever flows
            // back, every SyntheticTeamClient blocks at MoveNext). The
            // CreateHandler path skips the WAF client pipeline entirely and
            // talks straight to the in-memory transport, which is the
            // documented grpc-dotnet integration-test pattern.
            //
            // The ResponseVersionHandler wrap is also required: TestServer's
            // in-memory transport returns HTTP 1.1 responses by default even
            // when the request was HTTP/2; gRPC validates response version
            // and aborts the call as a "Bad gRPC response" otherwise. Setting
            // response.Version = request.Version inside a DelegatingHandler
            // is the documented workaround
            // (renatogolia.com/2021/12/19, dotnet/aspnetcore source).
            for (var i = 0; i < teamCount; i++)
            {
                var handler = new ResponseVersionHandler
                {
                    InnerHandler = _factory.Server.CreateHandler(),
                };
                channels[i] = GrpcChannel.ForAddress(
                    "http://localhost",
                    new GrpcChannelOptions { HttpHandler = handler });

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

    /// <summary>
    /// DelegatingHandler that copies the request's HTTP version onto the
    /// response. Required because <c>TestServer.CreateHandler()</c> returns
    /// HTTP 1.1 on the response by default — even when the request was HTTP/2
    /// — and Grpc.Net.Client rejects non-HTTP/2 responses with a "Bad gRPC
    /// response" RpcException. Documented workaround:
    /// renatogolia.com/2021/12/19, mirrors the helper used in
    /// <c>dotnet/aspnetcore</c>'s own gRPC functional tests.
    /// </summary>
    private sealed class ResponseVersionHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Version = request.Version;
            return response;
        }
    }
}
