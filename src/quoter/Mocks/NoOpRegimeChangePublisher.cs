using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Schedule;
using Microsoft.Extensions.Logging;

namespace Bifrost.Quoter.Mocks;

/// <summary>
/// Build-green <see cref="IRegimeChangePublisher"/> that only logs. Replaced
/// by the RabbitMQ-backed publisher once the broker glue lands; the binding
/// swap in <c>Program.cs</c> is the only call site that changes.
/// </summary>
public sealed class NoOpRegimeChangePublisher : IRegimeChangePublisher
{
    private readonly ILogger<NoOpRegimeChangePublisher> _log;

    public NoOpRegimeChangePublisher(ILogger<NoOpRegimeChangePublisher> log)
    {
        _log = log;
    }

    public void Emit(RegimeTransition transition)
    {
        _log.LogDebug(
            "NoOp RegimeChange: {From} -> {To} (McForced={Mc}, Reason={Reason})",
            transition.From, transition.To, transition.McForced, transition.Reason);
    }
}
