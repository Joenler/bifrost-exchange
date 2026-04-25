using Bifrost.Gateway.Tests.Fixtures;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bifrost.Gateway.Tests.Streaming;

/// <summary>
/// Pitfall 1 regression guard. Kestrel HTTP/2 KeepAlivePingDelay defaults to
/// <see cref="TimeSpan.MaxValue"/> (DISABLED). The GW-07 mass-cancel-on-disconnect
/// SLO depends on those PINGs firing so the gateway notices half-open TCP connections.
/// <see cref="Program"/> sets explicit values via appsettings.json AND a defensive
/// override; this test asserts the resolved value is finite.
///
/// The check reads <see cref="IOptions{KestrelServerOptions}"/> from DI — that's
/// the same Options snapshot the running Kestrel transport uses, so the values
/// here are exactly what wire-level pings will follow.
/// </summary>
[Collection("Gateway")]
public sealed class KeepAlivePingTests : IClassFixture<GatewayTestHost>
{
    private readonly GatewayTestHost _host;

    public KeepAlivePingTests(GatewayTestHost host) => _host = host;

    [Fact]
    public void KestrelHttp2KeepAlivePingDelay_NotDisabled()
    {
        var opts = _host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.NotEqual(TimeSpan.MaxValue, opts.Limits.Http2.KeepAlivePingDelay);
        // Sanity bound — should be on the order of seconds for a LAN-internal gRPC server.
        Assert.True(opts.Limits.Http2.KeepAlivePingDelay <= TimeSpan.FromSeconds(60),
            $"KeepAlivePingDelay should be ≤ 60s, got {opts.Limits.Http2.KeepAlivePingDelay}");
    }

    [Fact]
    public void KestrelHttp2KeepAlivePingTimeout_NotDisabled()
    {
        var opts = _host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.NotEqual(TimeSpan.MaxValue, opts.Limits.Http2.KeepAlivePingTimeout);
        Assert.True(opts.Limits.Http2.KeepAlivePingTimeout <= TimeSpan.FromSeconds(60),
            $"KeepAlivePingTimeout should be ≤ 60s, got {opts.Limits.Http2.KeepAlivePingTimeout}");
    }
}
