using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Bifrost.Orchestrator.Tests.TestSupport;

/// <summary>
/// Represents a single captured <c>BasicPublishAsync</c> call on the test
/// <see cref="IChannel"/>. The harness preserves publishes in call order so
/// assertions can verify sequencing and routing-key mapping.
/// </summary>
public sealed class CapturedPublish
{
    public required string Exchange { get; init; }

    public required string RoutingKey { get; init; }

    public required bool Mandatory { get; init; }

    public required byte[] Body { get; init; }

    /// <summary>UTF-8 string representation of <see cref="Body"/>.</summary>
    public string BodyAsString => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// In-memory fixture that implements enough of <see cref="IChannel"/> to
/// capture <c>BasicPublishAsync</c> calls for subsequent assertion. The
/// harness is NOT a general RabbitMQ simulator: it intentionally implements
/// only the two channel members the orchestrator's topology + publisher
/// exercise (<c>ExchangeDeclareAsync</c>, <c>BasicPublishAsync</c>) and
/// throws <see cref="NotSupportedException"/> on every other member so
/// future misuse surfaces immediately rather than silently passing.
/// </summary>
/// <remarks>
/// Rationale for the hand-rolled stub rather than a generated mock: the
/// project does not currently pin Moq / NSubstitute in
/// <c>Directory.Packages.props</c>; introducing a test-only package just
/// to mock one method would be disproportionate churn for a single plan's
/// test footprint. The RabbitMQ.Client 7.x <see cref="IChannel"/> surface
/// changes rarely; if it grows a member a future plan's publisher starts
/// using, the missing override will be a clear compile error at the point
/// of first use.
/// </remarks>
public sealed class RabbitMqSubscriberHarness
{
    private readonly List<CapturedPublish> _captured = new();

    public RabbitMqSubscriberHarness()
    {
        Channel = new CapturingChannel(this);
    }

    /// <summary>The fake channel callers pass into the publisher under test.</summary>
    public IChannel Channel { get; }

    /// <summary>
    /// All publishes captured in call order. Each entry is an independent
    /// snapshot — safe to iterate while the publisher is still active.
    /// </summary>
    public IReadOnlyList<CapturedPublish> Captured => _captured;

    /// <summary>
    /// Filter the captured publishes to those whose routing key starts with
    /// <paramref name="prefix"/>. Useful for asserting the subset bound to a
    /// given topic pattern (e.g. <c>"round.state."</c>).
    /// </summary>
    public IReadOnlyList<CapturedPublish> CapturedWithRoutingPrefix(string prefix) =>
        _captured
            .Where(c => c.RoutingKey.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Drop every captured publish so far. Useful when the actor under test
    /// emits a reconciliation envelope on boot and the assertion only cares
    /// about post-boot publishes — clear the harness after StartAsync settles
    /// and before the test body's first WriteAsync.
    /// </summary>
    public void Clear() => _captured.Clear();

    private void Capture(CapturedPublish publish) => _captured.Add(publish);

    private sealed class CapturingChannel : IChannel
    {
        private readonly RabbitMqSubscriberHarness _owner;

        public CapturingChannel(RabbitMqSubscriberHarness owner)
        {
            _owner = owner;
        }

        // --- Implemented members ---

        public ValueTask BasicPublishAsync<TProperties>(
            string exchange,
            string routingKey,
            bool mandatory,
            TProperties basicProperties,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken = default)
            where TProperties : IReadOnlyBasicProperties, IAmqpHeader
        {
            _owner.Capture(new CapturedPublish
            {
                Exchange = exchange,
                RoutingKey = routingKey,
                Mandatory = mandatory,
                Body = body.ToArray(),
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask BasicPublishAsync<TProperties>(
            CachedString exchange,
            CachedString routingKey,
            bool mandatory,
            TProperties basicProperties,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken = default)
            where TProperties : IReadOnlyBasicProperties, IAmqpHeader
        {
            _owner.Capture(new CapturedPublish
            {
                Exchange = exchange.Value,
                RoutingKey = routingKey.Value,
                Mandatory = mandatory,
                Body = body.ToArray(),
            });
            return ValueTask.CompletedTask;
        }

        public Task ExchangeDeclareAsync(
            string exchange,
            string type,
            bool durable,
            bool autoDelete,
            IDictionary<string, object?>? arguments,
            bool passive = false,
            bool noWait = false,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        // --- IDisposable / IAsyncDisposable (no resources to release) ---

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Dispose() { }

        // --- Properties (only read the ones the publisher does not touch) ---

        public int ChannelNumber => throw NotSupported(nameof(ChannelNumber));

        public ShutdownEventArgs? CloseReason => null;

        public IAsyncBasicConsumer? DefaultConsumer { get; set; }

        public bool IsClosed => false;

        public bool IsOpen => true;

        public string? CurrentQueue => null;

        public TimeSpan ContinuationTimeout { get; set; } = TimeSpan.FromSeconds(20);

        // --- Events (declared but never raised — plan-06-05 publisher does
        //     not subscribe) ---

#pragma warning disable CS0067 // events declared but never used
        public event AsyncEventHandler<BasicAckEventArgs>? BasicAcksAsync;

        public event AsyncEventHandler<BasicNackEventArgs>? BasicNacksAsync;

        public event AsyncEventHandler<BasicReturnEventArgs>? BasicReturnAsync;

        public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;

        public event AsyncEventHandler<FlowControlEventArgs>? FlowControlAsync;

        public event AsyncEventHandler<ShutdownEventArgs>? ChannelShutdownAsync;
#pragma warning restore CS0067

        // --- NotSupported stubs — plan 06-05 publisher/topology never call
        //     these; a future plan that does will get an immediate
        //     NotSupportedException at the first call site. ---

        public Task CloseAsync(ushort replyCode, string replyText, bool abort, CancellationToken ct) =>
            throw NotSupported(nameof(CloseAsync));

        public Task CloseAsync(ShutdownEventArgs reason, bool abort) =>
            throw NotSupported(nameof(CloseAsync));

        public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken ct) =>
            throw NotSupported(nameof(CloseAsync));

        public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken ct = default) =>
            throw NotSupported(nameof(GetNextPublishSequenceNumberAsync));

        public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken ct = default) =>
            throw NotSupported(nameof(BasicAckAsync));

        public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken ct = default) =>
            throw NotSupported(nameof(BasicNackAsync));

        public Task BasicCancelAsync(string consumerTag, bool noWait = false, CancellationToken ct = default) =>
            throw NotSupported(nameof(BasicCancelAsync));

        public Task<string> BasicConsumeAsync(
            string queue,
            bool autoAck,
            string consumerTag,
            bool noLocal,
            bool exclusive,
            IDictionary<string, object?>? arguments,
            IAsyncBasicConsumer consumer,
            CancellationToken ct = default) =>
            throw NotSupported(nameof(BasicConsumeAsync));

        public Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck, CancellationToken ct = default) =>
            throw NotSupported(nameof(BasicGetAsync));

        public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken ct = default) =>
            throw NotSupported(nameof(BasicQosAsync));

        public ValueTask BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken ct = default) =>
            throw NotSupported(nameof(BasicRejectAsync));

        public Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken ct = default) =>
            throw NotSupported(nameof(ExchangeDeclarePassiveAsync));

        public Task ExchangeDeleteAsync(string exchange, bool ifUnused = false, bool noWait = false, CancellationToken ct = default) =>
            throw NotSupported(nameof(ExchangeDeleteAsync));

        public Task ExchangeBindAsync(
            string destination,
            string source,
            string routingKey,
            IDictionary<string, object?>? arguments,
            bool noWait = false,
            CancellationToken ct = default) =>
            throw NotSupported(nameof(ExchangeBindAsync));

        public Task ExchangeUnbindAsync(
            string destination,
            string source,
            string routingKey,
            IDictionary<string, object?>? arguments,
            bool noWait = false,
            CancellationToken ct = default) =>
            throw NotSupported(nameof(ExchangeUnbindAsync));

        public Task<QueueDeclareOk> QueueDeclareAsync(
            string queue,
            bool durable,
            bool exclusive,
            bool autoDelete,
            IDictionary<string, object?>? arguments,
            bool passive = false,
            bool noWait = false,
            CancellationToken ct = default) =>
            throw NotSupported(nameof(QueueDeclareAsync));

        public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string queue, CancellationToken ct = default) =>
            throw NotSupported(nameof(QueueDeclarePassiveAsync));

        public Task<uint> QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait = false, CancellationToken ct = default) =>
            throw NotSupported(nameof(QueueDeleteAsync));

        public Task<uint> QueuePurgeAsync(string queue, CancellationToken ct = default) =>
            throw NotSupported(nameof(QueuePurgeAsync));

        public Task QueueBindAsync(
            string queue,
            string exchange,
            string routingKey,
            IDictionary<string, object?>? arguments,
            bool noWait = false,
            CancellationToken ct = default) =>
            throw NotSupported(nameof(QueueBindAsync));

        public Task QueueUnbindAsync(
            string queue,
            string exchange,
            string routingKey,
            IDictionary<string, object?>? arguments,
            CancellationToken ct = default) =>
            throw NotSupported(nameof(QueueUnbindAsync));

        public Task<uint> MessageCountAsync(string queue, CancellationToken ct = default) =>
            throw NotSupported(nameof(MessageCountAsync));

        public Task<uint> ConsumerCountAsync(string queue, CancellationToken ct = default) =>
            throw NotSupported(nameof(ConsumerCountAsync));

        public Task TxCommitAsync(CancellationToken ct = default) =>
            throw NotSupported(nameof(TxCommitAsync));

        public Task TxRollbackAsync(CancellationToken ct = default) =>
            throw NotSupported(nameof(TxRollbackAsync));

        public Task TxSelectAsync(CancellationToken ct = default) =>
            throw NotSupported(nameof(TxSelectAsync));

        private static NotSupportedException NotSupported(string member) =>
            new($"RabbitMqSubscriberHarness.CapturingChannel: {member} is not implemented. " +
                "The harness implements only BasicPublishAsync + ExchangeDeclareAsync for plan 06-05.");
    }
}
