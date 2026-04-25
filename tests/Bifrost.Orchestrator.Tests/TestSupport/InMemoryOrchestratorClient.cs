using System.Threading.Channels;
using Bifrost.Contracts.Mc;
using Bifrost.Orchestrator.Actor;
using Bifrost.Orchestrator.Grpc;
using Bifrost.Time;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bifrost.Orchestrator.Tests.TestSupport;

/// <summary>
/// In-process gRPC client for orchestrator tests (CONTEXT D-30). Bypasses
/// Kestrel by directly invoking <see cref="OrchestratorServiceImpl.Execute"/>
/// and <see cref="OrchestratorServiceImpl.WatchRoundState"/> via a synthetic
/// <see cref="ServerCallContext"/>. Cuts out network startup, port binding,
/// and TLS plumbing — the test still exercises the full
/// validate-then-enqueue-then-await-TCS flow that production gRPC handlers
/// see.
/// </summary>
/// <remarks>
/// Construction expectation: callers ALSO need to stand up an
/// <see cref="OrchestratorActor"/> reading from the same
/// <see cref="ChannelWriter{T}"/>. Without an actor draining the channel,
/// <see cref="OrchestratorServiceImpl.Execute"/> will hang on the
/// <see cref="TaskCompletionSource{TResult}"/> until the ambient
/// <see cref="CancellationToken"/> fires.
/// </remarks>
public sealed class InMemoryOrchestratorClient
{
    public ChannelWriter<IOrchestratorMessage> Writer { get; }

    public RoundStateRingBuffer Ring { get; }

    public OrchestratorServiceImpl Impl { get; }

    public InMemoryOrchestratorClient(
        ChannelWriter<IOrchestratorMessage> writer,
        RoundStateRingBuffer ring,
        IClock clock,
        OrchestratorOptions? opts = null)
    {
        Writer = writer;
        Ring = ring;
        Impl = new OrchestratorServiceImpl(
            writer,
            ring,
            clock,
            Options.Create(opts ?? new OrchestratorOptions()),
            NullLogger<OrchestratorServiceImpl>.Instance);
    }

    public Task<McCommandResult> ExecuteAsync(McCommand request, CancellationToken ct = default) =>
        Impl.Execute(request, new TestServerCallContext(ct));
}

/// <summary>
/// Minimal <see cref="ServerCallContext"/> for in-process gRPC tests.
/// Implements only the surface
/// <see cref="OrchestratorServiceImpl"/> reads
/// (<see cref="ServerCallContext.CancellationToken"/> on every call;
/// nothing else). Throws on any other member access via the abstract
/// contract — there is no abstract member that requires an explicit
/// throw because we override every required core property below.
/// </summary>
internal sealed class TestServerCallContext : ServerCallContext
{
    public TestServerCallContext(CancellationToken ct)
    {
        _ct = ct;
    }

    private readonly CancellationToken _ct;

    protected override CancellationToken CancellationTokenCore => _ct;

    protected override string MethodCore => "/bifrost.mc.v1.OrchestratorService/Execute";

    protected override string HostCore => "localhost";

    protected override string PeerCore => "in-process";

    protected override DateTime DeadlineCore => DateTime.MaxValue;

    protected override Metadata RequestHeadersCore => new();

    protected override Metadata ResponseTrailersCore => new();

    protected override Status StatusCore { get; set; } = Status.DefaultSuccess;

    protected override WriteOptions? WriteOptionsCore { get; set; }

    protected override AuthContext AuthContextCore =>
        new(string.Empty, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(
        ContextPropagationOptions? options) =>
        throw new NotSupportedException(
            "TestServerCallContext does not propagate gRPC context — "
            + "in-process orchestrator tests do not call into other gRPC services.");

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
        Task.CompletedTask;
}
