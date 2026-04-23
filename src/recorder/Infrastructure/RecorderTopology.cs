namespace Bifrost.Recorder.Infrastructure;

/// <summary>
/// RabbitMQ topology constants the recorder binds to. Topic exchange name is
/// renamed from Arena's <c>trader.events.v1</c> to the BIFROST
/// <c>bifrost.events.v1</c> convention (see Plan 02-05 command-side rename).
/// Arena's <c>trader.metrics</c> exchange + recorder-metrics queue are DROPPED
/// — Phase 02 has no trader-metrics stream, so there is nothing to bind to.
/// Routing keys (<c>order.#</c>, <c>lifecycle.#</c>) stay identical.
/// </summary>
public static class RecorderTopology
{
    public const string TraderEventsExchange = "bifrost.events.v1";
    public const string RecorderEventsQueue = "bifrost.recorder.events.v1";
    public const string OrderRoutingKey = "order.#";
    public const string LifecycleRoutingKey = "lifecycle.#";
}
