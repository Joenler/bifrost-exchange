using Bifrost.Orchestrator;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: HTTP/2 only on 5006. The LAN-only deployment posture means no
// TLS — and Http1AndHttp2 without TLS falls back to HTTP/1.1 because ALPN
// is the only negotiation channel that actually wires h2 on cleartext.
// Pinning Http2 keeps gRPC routable.
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenAnyIP(5006, lo => lo.Protocols = HttpProtocols.Http2);
});

// Bind OrchestratorOptions from the "Orchestrator" appsettings section.
builder.Services.Configure<OrchestratorOptions>(
    builder.Configuration.GetSection("Orchestrator"));

// Register gRPC infrastructure. Service implementations are mapped by
// downstream plans; until then the server is bound but has no services
// mapped — Execute() RPCs return grpc-status UNIMPLEMENTED, which is
// exactly the expected behaviour for the SDK-flip plumbing pass.
builder.Services.AddGrpc();

// Sentinel-file writer for the docker-compose healthcheck. Preserved
// verbatim from the Phase 00 skeleton so BOOT-03 stays green.
builder.Services.AddHostedService<StartupLogger>();

var app = builder.Build();

await app.RunAsync();
