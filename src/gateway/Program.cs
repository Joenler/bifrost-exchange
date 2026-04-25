using Bifrost.Gateway;
using Bifrost.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// Pitfall 1 (07-RESEARCH.md §Common Pitfalls): Kestrel HTTP/2 KeepAlivePingDelay defaults
// to TimeSpan.MaxValue (DISABLED). GW-07 mass-cancel-on-disconnect SLO depends on these
// pings firing. appsettings.json sets explicit values; this defensive override guards
// against configuration-load failures leaving the framework defaults in place.
builder.WebHost.ConfigureKestrel(o =>
{
    if (o.Limits.Http2.KeepAlivePingDelay == TimeSpan.MaxValue)
        o.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
    if (o.Limits.Http2.KeepAlivePingTimeout == TimeSpan.MaxValue)
        o.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 4 * 1024 * 1024;
    options.MaxSendMessageSize = 4 * 1024 * 1024;
});

// Bifrost.Time DI (Phase 00 convention; CI lint fence bans DateTime.UtcNow).
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(TimeProvider.System);

// Phase 00 sentinel: writes /tmp/bifrost-ready when the host is up.
builder.Services.AddHostedService<StartupLogger>();

var app = builder.Build();
app.UseRouting();
// D-12: prometheus-net /metrics endpoint on the same Kestrel as gRPC.
app.MapMetrics();
// gRPC service registration is added in Plan 05 (StrategyGatewayService).
// app.MapGrpcService<StrategyGatewayService>();
app.Run();
