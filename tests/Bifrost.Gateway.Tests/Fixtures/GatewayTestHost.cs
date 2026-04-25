using System.Collections.Concurrent;
using Bifrost.Contracts.Internal.Commands;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Gateway.Rabbit;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bifrost.Gateway.Tests.Fixtures;

/// <summary>
/// In-process gateway host for unit-style integration tests. Replaces:
///   - <c>IRoundStateSource</c> with a mutable shim tests can drive,
///   - <c>IGatewayCommandPublisher</c> with a recording stub that captures
///     every published command,
///   - <c>IConnection</c> registration (kept as a placeholder factory that
///     is never invoked because nothing else now asks for it).
///
/// This avoids the need to stub the 30-member RabbitMQ.Client 7.x IChannel
/// surface; the publisher boundary is the right abstraction (Plan 09 load
/// harness will use this same seam with a real Testcontainers broker).
///
/// The fixture does NOT bind to a real TCP port — <c>WebApplicationFactory</c>
/// uses an in-memory transport, so no port-contention serialization is
/// required at the OS level. The <c>[Collection("Gateway")]</c> + <c>DisableParallelization</c>
/// pair below still serializes test runs because the recording publisher
/// + mutable round-state are class-fixture singletons; sharing them across
/// concurrent tests would race.
/// </summary>
public sealed class GatewayTestHost : WebApplicationFactory<Program>
{
    public MutableRoundStateSource Round { get; } = new();
    public RecordingGatewayCommandPublisher CommandPublisher { get; } = new();

    private readonly string _emptyGuardsPath = Path.Combine(Path.GetTempPath(), $"bifrost-gateway-guards-{Guid.NewGuid():N}.absent.json");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Use an absent path so GuardThresholds.LoadFromFile falls back to Defaults()
        // (the loader returns Defaults() when File.Exists(path) == false). An empty
        // tempfile path would parse-fail; an absent path is the safe knob.
        builder.UseEnvironment("Test");
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Guards:ConfigPath"] = _emptyGuardsPath,
            ["RoundState:Initial"] = "RoundOpen",
        }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRoundStateSource>();
            services.AddSingleton<IRoundStateSource>(Round);

            services.RemoveAll<IGatewayCommandPublisher>();
            services.AddSingleton<IGatewayCommandPublisher>(CommandPublisher);

            // Plan 06+07: RabbitMQ-backed HostedServices (4 Plan-06 consumers,
            // ForecastDispatcher, HeartbeatService) all depend on IConnection.
            // The default DI factory eagerly resolves a real RabbitMQ connection,
            // which we cannot do in-process. Drop every gateway HostedService whose
            // namespace starts with Bifrost.Gateway.Rabbit OR Bifrost.Gateway.Dispatch
            // so tests don't trip the IConnection resolution at host start. The
            // dedicated ConsumerAuditTests + ForecastDispatcherTests + HeartbeatService
            // tests cover source-level invariants without needing the live services.
            var hostedDescriptors = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                            && d.ImplementationType is not null
                            && d.ImplementationType.Namespace is { } ns
                            && (ns.StartsWith("Bifrost.Gateway.Rabbit", StringComparison.Ordinal)
                                || ns.StartsWith("Bifrost.Gateway.Dispatch", StringComparison.Ordinal)))
                .ToList();
            foreach (var d in hostedDescriptors) services.Remove(d);
        });
    }

    /// <summary>Build a gRPC channel against the in-memory test server.</summary>
    public GrpcChannel CreateGrpcChannel()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        return GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = client,
        });
    }

    /// <summary>
    /// Mutable in-memory <see cref="IRoundStateSource"/> for tests that need to
    /// drive the round-state transitions the GuardChain reads.
    /// </summary>
    public sealed class MutableRoundStateSource : IRoundStateSource
    {
        public RoundState Current { get; private set; } = RoundState.RoundOpen;

        public event EventHandler<RoundStateChangedEventArgs>? OnChange;

        public void SetState(RoundState s)
        {
            var previous = Current;
            Current = s;
            OnChange?.Invoke(this, new RoundStateChangedEventArgs(previous, s, 0L));
        }
    }

    /// <summary>
    /// Recording stub for <see cref="IGatewayCommandPublisher"/>. Tests use the
    /// captured collections to assert the gateway translated and published the
    /// expected command after the GuardChain accepted.
    /// </summary>
    public sealed class RecordingGatewayCommandPublisher : IGatewayCommandPublisher
    {
        public ConcurrentQueue<(string ClientId, SubmitOrderCommand Cmd, string CorrelationId)> Submits { get; } = new();
        public ConcurrentQueue<(string ClientId, CancelOrderCommand Cmd, string CorrelationId)> Cancels { get; } = new();
        public ConcurrentQueue<(string ClientId, ReplaceOrderCommand Cmd, string CorrelationId)> Replaces { get; } = new();

        public ValueTask PublishSubmitOrderAsync(string clientId, SubmitOrderCommand cmd, string correlationId, CancellationToken ct = default)
        {
            Submits.Enqueue((clientId, cmd, correlationId));
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishCancelOrderAsync(string clientId, CancelOrderCommand cmd, string correlationId, CancellationToken ct = default)
        {
            Cancels.Enqueue((clientId, cmd, correlationId));
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishReplaceOrderAsync(string clientId, ReplaceOrderCommand cmd, string correlationId, CancellationToken ct = default)
        {
            Replaces.Enqueue((clientId, cmd, correlationId));
            return ValueTask.CompletedTask;
        }
    }
}
