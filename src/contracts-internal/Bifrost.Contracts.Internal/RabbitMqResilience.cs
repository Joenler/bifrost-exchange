using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using RabbitMQ.Client.Exceptions;

namespace Bifrost.Contracts.Internal;

public static class RabbitMqResilience
{
    public static ResiliencePipeline CreateConnectionPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 9,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = TimeSpan.FromSeconds(20),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder()
                    .Handle<BrokerUnreachableException>(),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "RabbitMQ not reachable, retry {Attempt}/9 in {Delay:F1}s",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalSeconds);
                    return default;
                }
            })
            .Build();
    }
}
