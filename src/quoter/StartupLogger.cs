using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bifrost.Quoter;

public sealed class StartupLogger(ILogger<StartupLogger> logger) : BackgroundService
{
    private const string ServiceName = "bifrost-quoter";
    private const string SentinelPath = "/tmp/bifrost-ready";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Service} started", ServiceName);
        Console.WriteLine($"{ServiceName} started");

        await File.WriteAllTextAsync(SentinelPath, "ready", stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on SIGTERM — IHostApplicationLifetime default wiring handles propagation.
        }
    }
}
