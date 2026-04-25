using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Gateway.State;
using Bifrost.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StrategyProto = Bifrost.Contracts.Strategy;

namespace Bifrost.Gateway.Dispatch;

/// <summary>
/// Dispatches forecast arrival events to teams on a cohort-staggered schedule.
/// Each tick dispatches to one cohort, rotating round-robin across cohortCount groups.
///
/// Donated VERBATIM from Arena/src/trader/ArenaTrader.Host/ForecastDispatcher.cs
/// with EXACTLY 4 surgical rewrites (see <c>UPSTREAM.md</c> for the canonical list).
///
/// The five jitter mechanisms are preserved byte-for-byte:
///   1. Cohort assignment by stable hash of teamName (CohortAssignment.CohortFor)
///   2. Round-robin: _currentCohort = (_currentCohort + 1) % _cohortCount
///   3. Start jitter: a one-time delay drawn from [0, _cohortStartJitter]
///   4. Intra-cohort spread: per-member delay drawn from [0, _intraCohortDispatchSpread]
///   5. Inter-tick jitter: symmetric per-tick variation around _cohortInterval
///
/// The cohort jitter is INTENTIONAL non-determinism (GW-08, CONTEXT D-02 + README.md).
/// Phase 12 must NOT zero the jitter knobs to "fix" determinism — see README.md.
///
/// Forecast source: this dispatcher subscribes to <see cref="RabbitMqTopology.PublicExchange"/>
/// with binding key <c>public.forecast</c> (Phase 04 IMB-01 publishes there). The bound
/// AsyncEventingBasicConsumer updates a volatile <see cref="ForecastSnapshot"/> field;
/// the dispatch loop reads that snapshot when emitting to each cohort member.
/// </summary>
public sealed class ForecastDispatcher : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConnection _connection;
    private readonly TeamRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly IClock _clock;
    private readonly ILogger<ForecastDispatcher> _logger;
    private readonly int _cohortCount;
    private readonly TimeSpan _cohortInterval;
    private readonly TimeSpan _cohortStartJitter;
    private readonly TimeSpan _intraCohortDispatchSpread;
    private readonly TimeSpan _interTickJitter;
    // Surgical rewrite (b): seeded RNG (NOT the BCL global). Jitter is unique per gateway
    // start (clock^processId seed) but reproducible within a single run via the
    // SetJitterRngForTest internal seam. CI lint fence in build/BannedSymbols.txt enforces
    // that no other dispatch path uses the global. Non-readonly so the test seam can pin
    // determinism; production code never mutates this field after the constructor.
    private Random _jitterRng;

    private PeriodicTimer? _timer;
    private int _currentCohort;
    private IChannel? _forecastChannel;
    private volatile ForecastSnapshot? _latestForecast;

    public ForecastDispatcher(
        IConnection connection,
        TeamRegistry registry,
        TimeProvider timeProvider,
        IClock clock,
        IConfiguration configuration,
        ILogger<ForecastDispatcher> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _cohortCount = configuration.GetValue("Gateway:ForecastDispatch:CohortCount", 3);
        if (_cohortCount <= 0) _cohortCount = 3;
        _cohortInterval = TimeSpan.FromMilliseconds(
            configuration.GetValue("Gateway:ForecastDispatch:CohortIntervalMs", 15_000));
        _cohortStartJitter = TimeSpan.FromMilliseconds(
            configuration.GetValue("Gateway:ForecastDispatch:CohortStartJitterMs", 1_000));
        _intraCohortDispatchSpread = TimeSpan.FromMilliseconds(
            configuration.GetValue("Gateway:ForecastDispatch:IntraCohortDispatchSpreadMs", 500));
        _interTickJitter = TimeSpan.FromMilliseconds(
            configuration.GetValue("Gateway:ForecastDispatch:InterTickJitterMs", 200));

        // Surgical rewrite (b) seeding: clock.UtcTicks XOR processId, masked to 32 bits.
        // Tests override _jitterRng via the internal SetJitterRngForTest below.
        var seedSource = (long)_clock.GetUtcNow().UtcTicks ^ Environment.ProcessId;
        _jitterRng = new Random((int)(seedSource & 0xFFFFFFFF));
    }

    /// <summary>Test seam: override the jitter RNG with a deterministic seed for tests.</summary>
    internal void SetJitterRngForTest(Random rng) =>
        _jitterRng = rng ?? throw new ArgumentNullException(nameof(rng));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pitfall 6: dedicated channel for the forecast subscription.
        _forecastChannel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await _forecastChannel.ExchangeDeclareAsync(
            RabbitMqTopology.PublicExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: stoppingToken);

        const string queueName = "bifrost.gateway.forecast";
        await _forecastChannel.QueueDeclareAsync(
            queueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: stoppingToken);
        await _forecastChannel.QueueBindAsync(
            queueName,
            RabbitMqTopology.PublicExchange,
            "public.forecast",
            cancellationToken: stoppingToken);

        // PUSH, not poll — Pitfall 9.
        var consumer = new AsyncEventingBasicConsumer(_forecastChannel);
        consumer.ReceivedAsync += (_, ea) =>
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope<JsonElement>>(ea.Body.Span, JsonOptions);
                if (envelope is null) return Task.CompletedTask;
                if (envelope.MessageType != MessageTypes.ForecastUpdate) return Task.CompletedTask;
                var dto = envelope.Payload.Deserialize<ForecastUpdateEvent>(JsonOptions);
                if (dto is null) return Task.CompletedTask;
                _latestForecast = new ForecastSnapshot(
                    ForecastPriceTicks: dto.ForecastPriceTicks,
                    HorizonNs: dto.HorizonNs,
                    OriginUtc: envelope.TimestampUtc,
                    Sequence: envelope.Sequence ?? 0L);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ForecastDispatcher: failed to parse public.forecast envelope");
            }
            return Task.CompletedTask;
        };
        await _forecastChannel.BasicConsumeAsync(
            queueName,
            autoAck: true,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _currentCohort = 0;
        _timer = new PeriodicTimer(_cohortInterval, _timeProvider);

        // Surgical rewrite (b) jitter site #1: the one-time start jitter (Arena lines ~89-94).
        var startDelay = TimeSpan.Zero;
        if (_cohortStartJitter > TimeSpan.Zero)
        {
            var jitterMs = _jitterRng.NextDouble() * _cohortStartJitter.TotalMilliseconds;
            startDelay = TimeSpan.FromMilliseconds(jitterMs);
            _logger.LogInformation(
                "ForecastDispatcher cohortInterval={IntervalMs}ms startJitter={JitterMs}ms (drawn {DrawMs:F0}ms)",
                _cohortInterval.TotalMilliseconds, _cohortStartJitter.TotalMilliseconds, jitterMs);
        }

        await RunDispatchLoop(stoppingToken, startDelay);
    }

    internal Task RunDispatchLoop(CancellationToken ct) => RunDispatchLoop(ct, TimeSpan.Zero);

    internal async Task RunDispatchLoop(CancellationToken ct, TimeSpan startDelay)
    {
        try
        {
            if (startDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(startDelay, _timeProvider, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            // When interTickJitter is configured, use Task.Delay with a randomly
            // varied interval on each iteration instead of a fixed PeriodicTimer.
            // This breaks wall-clock alignment: PeriodicTimer ticks are anchored
            // to construction time so they land on a fixed phase forever; adding
            // per-tick jitter lets the cohort phase drift over time so the bursts
            // don't all land on the same wall-clock seconds (Arena debug/cj-framework
            // -book-dynamics-2026-04-15.md H4).
            if (_interTickJitter > TimeSpan.Zero)
            {
                while (!ct.IsCancellationRequested)
                {
                    var baseMs = _cohortInterval.TotalMilliseconds;
                    var jitterRangeMs = _interTickJitter.TotalMilliseconds;
                    // Surgical rewrite (b) jitter site #2: symmetric jitter around baseline [-j/2, +j/2].
                    var jitterMs = (_jitterRng.NextDouble() - 0.5) * jitterRangeMs;
                    var nextMs = Math.Max(500.0, baseMs + jitterMs);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(nextMs), _timeProvider, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    await DispatchOneTickAsync(ct);
                }
            }
            else
            {
                while (await _timer!.WaitForNextTickAsync(ct))
                {
                    await DispatchOneTickAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task DispatchOneTickAsync(CancellationToken ct)
    {
        // Surgical rewrite (c): TeamRegistry snapshot replaces Arena's static
        // strategy-registration list. See UPSTREAM.md.
        var teamsThisTick = _registry.SnapshotAll();
        // Filter to current cohort for this tick (the round-robin invariant).
        var cohortMembers = new List<TeamState>();
        for (var i = 0; i < teamsThisTick.Length; i++)
        {
            var team = teamsThisTick[i];
            if (CohortAssignment.CohortFor(team.TeamName, _cohortCount) != _currentCohort) continue;
            cohortMembers.Add(team);
        }

        var forecast = _latestForecast;   // volatile read
        if (forecast is null)
        {
            // No forecast received yet — still rotate cohort so the round-robin invariant holds.
            _currentCohort = (_currentCohort + 1) % _cohortCount;
            return;
        }

        for (var i = 0; i < cohortMembers.Count; i++)
        {
            var team = cohortMembers[i];

            // Surgical rewrite (b) jitter site #3: intra-cohort spread (Arena lines ~225-238).
            // Delay each strategy after the first by a random fraction of the configured spread.
            // Keeps total dispatch latency bounded below the cohort interval while breaking
            // the simultaneous-fire pattern that creates a thundering herd.
            if (i > 0 && _intraCohortDispatchSpread > TimeSpan.Zero)
            {
                var spreadMs = _jitterRng.NextDouble() * _intraCohortDispatchSpread.TotalMilliseconds;
                if (spreadMs > 0)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(spreadMs), _timeProvider, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            // Surgical rewrite (d): replace aware.OnForecastArrival(...) with the
            // ring-Append + per-team Outbound.WriteAsync sequence. Pitfall 10:
            // ring-Append happens UNDER the team's StateLock; the channel write
            // happens OUTSIDE the lock.
            var dispatchNow = _clock.GetUtcNow();
            var marketEvent = new StrategyProto.MarketEvent
            {
                Sequence = forecast.Sequence,
                TimestampNs = dispatchNow.ToUnixTimeMilliseconds() * 1_000_000L,
                ForecastUpdate = new StrategyProto.ForecastUpdate
                {
                    ForecastPriceTicks = forecast.ForecastPriceTicks,
                    HorizonNs = forecast.HorizonNs,
                },
            };
            try
            {
                lock (team.StateLock)
                {
                    var wrap = new Envelope<object>(
                        MessageType: MessageTypes.ForecastUpdate,
                        TimestampUtc: dispatchNow,
                        CorrelationId: null,
                        ClientId: team.ClientId,
                        InstrumentId: null,
                        Sequence: forecast.Sequence,
                        Payload: marketEvent);
                    team.Ring.Append(wrap);
                }
                if (team.Outbound is { } writer)
                {
                    try
                    {
                        await writer.WriteAsync(marketEvent, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Team disconnected mid-tick — skip this team but keep dispatching the rest.
                    }
                }

                // Plan 08 will land GatewayMetrics.ForecastsDispatched.Inc(team.TeamName) here.
                // For now the metric is a no-op TODO.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForecastDispatcher: dispatch failed for team {Team}", team.TeamName);
            }
        }

        _currentCohort = (_currentCohort + 1) % _cohortCount;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        if (_forecastChannel is not null)
        {
            try
            {
                await _forecastChannel.CloseAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ForecastDispatcher channel close failed");
            }
            _forecastChannel.Dispose();
            _forecastChannel = null;
        }
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Cached snapshot of the most recent <c>public.forecast</c> envelope.
    /// Volatile read by the dispatch loop; volatile write by the consumer callback.
    /// </summary>
    internal sealed record ForecastSnapshot(
        long ForecastPriceTicks,
        long HorizonNs,
        DateTimeOffset OriginUtc,
        long Sequence);

    /// <summary>Test seam: pre-seed the latest forecast for unit tests.</summary>
    internal void SetLatestForecastForTest(long forecastPriceTicks, long horizonNs, DateTimeOffset originUtc, long sequence) =>
        _latestForecast = new ForecastSnapshot(forecastPriceTicks, horizonNs, originUtc, sequence);
}
