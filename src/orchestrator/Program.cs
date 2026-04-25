using System.Threading.Channels;
using Bifrost.Contracts.Internal;
using Bifrost.Orchestrator;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Orchestrator.News;
using Bifrost.Orchestrator.Rabbit;
using Bifrost.Orchestrator.State;
using Bifrost.Time;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: HTTP/2 only on 5006. The LAN-only deployment posture means no
// TLS - and Http1AndHttp2 without TLS falls back to HTTP/1.1 because ALPN
// is the only negotiation channel that actually wires h2 on cleartext.
// Pinning Http2 keeps gRPC routable.
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenAnyIP(5006, lo => lo.Protocols = HttpProtocols.Http2);
});

// Bind OrchestratorOptions from the "Orchestrator" appsettings section.
builder.Services.Configure<OrchestratorOptions>(
    builder.Configuration.GetSection("Orchestrator"));

// Core singletons + actor-loop wiring.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<RoundStateMachine>();
builder.Services.AddSingleton<JsonStateStore>();

// RabbitMQ connection + channel - uses the Phase 02 Polly retry pipeline so
// the orchestrator boot survives a slow-to-start broker. Creating the
// connection synchronously inside the factory keeps the registration a plain
// Singleton; the host startup waits on the pipeline's retries before the
// actor's StartAsync runs.
IConfigurationSection rabbitConfig = builder.Configuration.GetSection("RabbitMq");
builder.Services.AddSingleton<IConnection>(sp =>
{
    ConnectionFactory factory = new()
    {
        HostName = rabbitConfig["Host"] ?? "rabbitmq",
        Port = int.Parse(rabbitConfig["Port"] ?? "5672"),
        UserName = rabbitConfig["Username"] ?? "guest",
        Password = rabbitConfig["Password"] ?? "guest",
        VirtualHost = rabbitConfig["VirtualHost"] ?? "/",
    };

    ILogger logger = sp.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Bifrost.Orchestrator.Startup");
    ResiliencePipeline pipeline = RabbitMqResilience.CreateConnectionPipeline(logger);

    return pipeline.ExecuteAsync(
        async ct => await factory.CreateConnectionAsync("bifrost-orchestrator", ct),
        CancellationToken.None).AsTask().GetAwaiter().GetResult();
});
builder.Services.AddSingleton<IChannel>(sp =>
    sp.GetRequiredService<IConnection>().CreateChannelAsync().GetAwaiter().GetResult());

builder.Services.AddSingleton<OrchestratorRabbitMqTopology>();
builder.Services.AddSingleton<OrchestratorPublisher>();

// Collaborator stubs landed here; follow-up plans fill in the bodies WITHOUT
// altering the OrchestratorActor constructor.
builder.Services.AddSingleton<RoundStateRingBuffer>();
builder.Services.AddSingleton(sp =>
    new RoundSeedAllocator(
        sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value.MasterSeed));
builder.Services.AddSingleton<INewsLibrary, FileSystemNewsLibrary>();

// Actor channel: bounded 256, FullMode=Wait so backpressure propagates to gRPC
// handlers (they block on WriteAsync when full); SingleReader=true because the
// actor drain loop is the sole consumer.
builder.Services.AddSingleton(Channel.CreateBounded<IOrchestratorMessage>(
    new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    }));
builder.Services.AddSingleton<ChannelWriter<IOrchestratorMessage>>(sp =>
    sp.GetRequiredService<Channel<IOrchestratorMessage>>().Writer);
builder.Services.AddSingleton<ChannelReader<IOrchestratorMessage>>(sp =>
    sp.GetRequiredService<Channel<IOrchestratorMessage>>().Reader);

builder.Services.AddHostedService<OrchestratorActor>();

// Register gRPC infrastructure. Service implementations are mapped by a
// follow-up plan; until then the server is bound but has no services mapped -
// Execute() RPCs return grpc-status UNIMPLEMENTED, which is exactly the
// expected behaviour for the plumbing pass.
builder.Services.AddGrpc();

// Sentinel-file writer for the docker-compose healthcheck. Preserved verbatim
// from the Phase 00 skeleton so BOOT-03 stays green.
builder.Services.AddHostedService<StartupLogger>();

WebApplication app = builder.Build();

await app.RunAsync();
