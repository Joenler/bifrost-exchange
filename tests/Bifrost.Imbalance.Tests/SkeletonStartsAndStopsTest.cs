using Bifrost.Imbalance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bifrost.Imbalance.Tests;

public sealed class SkeletonStartsAndStopsTest
{
    [Fact]
    public async Task Host_Builds_Starts_Stops_Without_Error()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHostedService<StartupLogger>();

        using var host = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await host.StartAsync(cts.Token);
        await host.StopAsync(CancellationToken.None);
    }
}
