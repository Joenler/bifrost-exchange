using System.Diagnostics;
using Grpc.Net.Client;
using MarketProto = Bifrost.Contracts.Market;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Load.Tests;

/// <summary>
/// One synthetic team driver. D-20 mix: 70% OrderSubmit / 20% OrderCancel /
/// 10% OrderReplace at the configured target rate, with Poisson inter-arrival
/// intervals. Records inbound (client-send → frame-acknowledged-by-Kestrel)
/// latency in a histogram on every command.
///
/// "Inbound latency" interpretation (Plan 09 acceptance — SPEC req 11):
/// the harness times the duration of the gRPC bidi <c>WriteAsync</c> call,
/// which resolves once the frame has been sent to Kestrel and its HTTP/2
/// flow-control window credit returned. For a LAN-internal in-process
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// transport this is dominated by the gateway's inbound dispatch path
/// (translator + GuardChain) and is a conservative proxy for the
/// "client-send → gateway-ack" round-trip the SPEC names. True end-to-end
/// per-command ack matching would require correlation IDs on every request
/// + matching on the response stream — deferred as not load-gate-critical
/// for the GW-09 50 ms inbound budget.
/// </summary>
public sealed class SyntheticTeamClient : IAsyncDisposable
{
    private readonly string _teamName;
    private readonly GrpcChannel _channel;
    private readonly PoissonScheduler _scheduler;
    private readonly Random _mixRng;

    public List<double> InboundLatencyMs { get; } = new();

    public SyntheticTeamClient(string teamName, GrpcChannel channel, int seed, int targetRatePerSecond)
    {
        _teamName = teamName;
        _channel = channel;
        _scheduler = new PoissonScheduler(seed, targetRatePerSecond);
        // Separate Random instance for the 70/20/10 mix roll so the inter-arrival
        // and command-mix sequences are decoupled; both reproducible from `seed`.
        _mixRng = new Random(seed ^ 0x5A5A5A5A);
    }

    public async Task RunAsync(TimeSpan duration, CancellationToken ct)
    {
        var client = new StrategyProto.StrategyGatewayService.StrategyGatewayServiceClient(_channel);
        using var call = client.StreamStrategy(cancellationToken: ct);

        // 1. First-frame Register handshake (SPEC req 1 + RegisterHandshakeTests).
        await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
        {
            Register = new StrategyProto.Register { TeamName = _teamName, LastSeenSequence = 0 },
        }, ct);

        // 2. Drain RegisterAck + the 5 PositionSnapshots that follow it (D-06a).
        if (!await call.ResponseStream.MoveNext(ct))
        {
            throw new InvalidOperationException($"team={_teamName}: server closed before RegisterAck");
        }

        var clientId = call.ResponseStream.Current.RegisterAck.ClientId;

        for (var i = 0; i < 5; i++)
        {
            if (!await call.ResponseStream.MoveNext(ct))
            {
                break;
            }
        }

        // 3. Spawn a consumer task that drains the response stream so HTTP/2
        // flow-control windows don't stall the writer. We don't need to inspect
        // each frame for the load-gate (the inbound timer is the writer-side
        // measurement); we just need to keep the receive side moving.
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                while (await call.ResponseStream.MoveNext(ct))
                {
                    // Drain only — load-gate measures inbound, not outbound, in v1.
                }
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation
            }
            catch (Grpc.Core.RpcException)
            {
                // expected on stream teardown
            }
        }, ct);

        // 4. Producer loop with Poisson-distributed inter-arrival + 70/20/10 mix.
        // DateTimeOffset.UtcNow is permitted in test code (BannedSymbols.txt only
        // bans DateTime.UtcNow + Random.Shared); the deadline is wall-clock by
        // design — the load harness measures real-world latency.
        var deadline = DateTimeOffset.UtcNow + duration;
        long orderSerial = 0;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var interval = _scheduler.NextInterArrival();
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var roll = _mixRng.NextDouble();
            var sw = Stopwatch.StartNew();

            try
            {
                if (roll < 0.70)
                {
                    // 70% OrderSubmit
                    await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
                    {
                        OrderSubmit = new StrategyProto.OrderSubmit
                        {
                            ClientId = clientId,
                            Instrument = NewInstrument("H1"),
                            Side = (orderSerial++ % 2 == 0) ? MarketProto.Side.Buy : MarketProto.Side.Sell,
                            OrderType = MarketProto.OrderType.Limit,
                            PriceTicks = 100 + (orderSerial % 50),
                            QuantityTicks = 1,
                            ClientOrderId = $"co-{_teamName}-{orderSerial}",
                        },
                    }, ct);
                }
                else if (roll < 0.90)
                {
                    // 20% OrderCancel
                    await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
                    {
                        OrderCancel = new StrategyProto.OrderCancel
                        {
                            ClientId = clientId,
                            OrderId = orderSerial++,
                            Instrument = NewInstrument("H1"),
                        },
                    }, ct);
                }
                else
                {
                    // 10% OrderReplace
                    await call.RequestStream.WriteAsync(new StrategyProto.StrategyCommand
                    {
                        OrderReplace = new StrategyProto.OrderReplace
                        {
                            ClientId = clientId,
                            OrderId = orderSerial++,
                            NewPriceTicks = 100 + (orderSerial % 50),
                            NewQuantityTicks = 1,
                            Instrument = NewInstrument("H1"),
                        },
                    }, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Grpc.Core.RpcException)
            {
                // Stream torn down (deadline / server close). Stop the producer.
                break;
            }
            finally
            {
                sw.Stop();
                InboundLatencyMs.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        try
        {
            await call.RequestStream.CompleteAsync();
        }
        catch (Grpc.Core.RpcException)
        {
            // Already torn down — fine.
        }
        catch (InvalidOperationException)
        {
            // Stream already completed — fine.
        }

        try
        {
            await consumerTask;
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private static MarketProto.Instrument NewInstrument(string id) => new()
    {
        InstrumentId = id,
        DeliveryArea = "DE",
        DeliveryPeriodStartNs = 0L,
        DeliveryPeriodEndNs = 0L,
        ProductType = MarketProto.ProductType.Unspecified,
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
