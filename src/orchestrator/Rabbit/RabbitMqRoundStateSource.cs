using System.Text.Json;
using Bifrost.Contracts.Internal;
using Bifrost.Contracts.Internal.Events;
using Bifrost.Exchange.Application.RoundState;
using Bifrost.Time;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using BifrostState = Bifrost.Exchange.Application.RoundState.RoundState;

namespace Bifrost.Orchestrator.Rabbit;

/// <summary>
/// Production implementation of the Phase 02 <see cref="IRoundStateSource"/>
/// seam. Subscribes to the <see cref="OrchestratorRabbitMqTopology.RoundExchange"/>
/// (<c>bifrost.round.v1</c>) on routing pattern <c>round.state.#</c>, deserialises
/// every <see cref="Envelope{T}"/>-wrapped <see cref="RoundStateChangedPayload"/>,
/// updates <see cref="Current"/> and raises <see cref="OnChange"/> with
/// (previous, current, timestamp_ns).
/// </summary>
/// <remarks>
/// <para>
/// Shipped here for downstream services (exchange, quoter, imbalance, DAH,
/// gateway) to register in their own composition roots once Phase 06 is live.
/// The orchestrator itself does not consume <see cref="IRoundStateSource"/> —
/// it is the authoritative producer. Each downstream service replaces its
/// existing <c>ConfigRoundStateSource</c> / <c>InMemoryRoundStateSource</c>
/// registration with this class, supplying its own
/// <see cref="IChannel"/> from the per-service connection (RabbitMQ.Client 7.x
/// channels are not thread-safe, so the source's subscribe path must own a
/// dedicated channel — the actor's publish channel must NOT be shared).
/// </para>
/// <para>
/// Reconciliation handling: a <see cref="RoundStateChangedPayload.IsReconciliation"/>
/// message published on orchestrator restart MUST raise <see cref="OnChange"/>
/// even when the carried state matches <see cref="Current"/>, so consumers that
/// boot before the orchestrator's restart-publish reconcile their local view
/// of paused / blocked flags. The <c>previous != newState || isReconciliation</c>
/// guard preserves this.
/// </para>
/// <para>
/// Defensive parse: unrecognised state names (forward-compat with future
/// <see cref="BifrostState"/> additions on the publisher side) and malformed
/// JSON bodies are logged and skipped — a single bad message must not take
/// the consumer's poll loop down.
/// </para>
/// </remarks>
public sealed class RabbitMqRoundStateSource : IRoundStateSource, IAsyncDisposable
{
    /// <summary>
    /// Routing-pattern this source binds against on
    /// <see cref="OrchestratorRabbitMqTopology.RoundExchange"/>. Catches every
    /// state transition the orchestrator publishes (<c>round.state.iteration_open</c>,
    /// <c>round.state.auction_open</c>, <c>round.state.aborted</c>, ...).
    /// </summary>
    public const string RoundStateRoutingPattern = "round.state.#";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IChannel _channel;
    private readonly IClock _clock;
    private readonly ILogger<RabbitMqRoundStateSource> _logger;
    private BifrostState _current = BifrostState.IterationOpen;
    private string? _queueName;

    public BifrostState Current => _current;

    public event EventHandler<RoundStateChangedEventArgs>? OnChange;

    public RabbitMqRoundStateSource(
        IChannel channel,
        IClock clock,
        ILogger<RabbitMqRoundStateSource> logger)
    {
        _channel = channel;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Idempotent exchange-declare + transient-queue-declare + bind-on-pattern
    /// + <c>BasicConsumeAsync</c>. Call once at host startup; safe to run
    /// alongside <see cref="OrchestratorRabbitMqTopology.DeclareAsync"/> from
    /// the orchestrator's own composition root because RabbitMQ
    /// <c>ExchangeDeclareAsync</c> is idempotent when args match.
    /// </summary>
    /// <remarks>
    /// Queue name is per-instance unique (<c>bifrost.round-state-source.{guid}</c>)
    /// + <c>exclusive=true</c> + <c>autoDelete=true</c> so multiple consumers
    /// across services receive their own copy of every state-changed message
    /// (fan-out semantics on a topic exchange) and the queue is reaped when
    /// the connection drops. Matches the
    /// <see cref="Heartbeat.RabbitMqGatewayHeartbeatSource"/> queue-naming
    /// shape verbatim.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _channel.ExchangeDeclareAsync(
            OrchestratorRabbitMqTopology.RoundExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct);

        _queueName = $"bifrost.round-state-source.{Guid.NewGuid():N}";
        await _channel.QueueDeclareAsync(
            _queueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: ct);

        await _channel.QueueBindAsync(
            _queueName,
            OrchestratorRabbitMqTopology.RoundExchange,
            RoundStateRoutingPattern,
            arguments: null,
            cancellationToken: ct);

        AsyncEventingBasicConsumer consumer = new(_channel);
        consumer.ReceivedAsync += OnMessageReceivedAsync;

        await _channel.BasicConsumeAsync(
            _queueName,
            autoAck: true,
            consumer,
            cancellationToken: ct);

        _logger.LogInformation(
            "RoundStateSource subscribed on {Queue} ({Exchange}/{Pattern})",
            _queueName,
            OrchestratorRabbitMqTopology.RoundExchange,
            RoundStateRoutingPattern);
    }

    private Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        HandleMessageBytes(ea.Body.Span);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deserialise an envelope body and, if it carries a recognisable
    /// <see cref="RoundStateChangedPayload"/>, update <see cref="Current"/>
    /// and raise <see cref="OnChange"/>. Exposed at <c>internal</c> so the
    /// orchestrator-tests assembly can drive it directly without a live
    /// broker via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal void HandleMessageBytes(ReadOnlySpan<byte> body)
    {
        Envelope<RoundStateChangedPayload>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope<RoundStateChangedPayload>>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Dropping malformed RoundStateChanged envelope");
            return;
        }

        if (envelope?.Payload is not { } payload)
        {
            return;
        }

        if (!Enum.TryParse<BifrostState>(payload.State, ignoreCase: true, out BifrostState newState))
        {
            _logger.LogWarning(
                "Ignoring RoundStateChanged with unrecognised state {State}",
                payload.State);
            return;
        }

        BifrostState previous = _current;
        _current = newState;

        // Reconciliation publishes (orchestrator restart) MUST propagate even when
        // the carried state matches our cached Current — downstream consumers may
        // have booted before the orchestrator and need the reconciliation signal
        // to flip their flags. Otherwise only fire on actual transitions.
        if (previous != newState || payload.IsReconciliation)
        {
            long ts = _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000L;
            OnChange?.Invoke(this, new RoundStateChangedEventArgs(previous, newState, ts));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_queueName is not null)
        {
            try
            {
                await _channel.QueueDeleteAsync(_queueName, ifUnused: false, ifEmpty: false);
            }
            catch
            {
                // Best-effort shutdown — the broker reaps exclusive queues when
                // the channel/connection drops anyway.
            }

            _queueName = null;
        }
    }
}
