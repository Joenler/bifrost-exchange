using Bifrost.Recorder.Infrastructure;
using Xunit;

namespace Bifrost.Recorder.Tests;

/// <summary>
/// Locks the recorder's binding shape: the public-events exchange constant
/// and the events.# routing-key pattern declared by
/// <see cref="RecorderTopology"/>, plus a source-level grep on
/// <c>RabbitMqRecorderConsumer</c> proving the consumer issues a
/// <c>QueueBindAsync</c> against the public-events exchange. Together the
/// three facts catch the regression where a future edit removes the
/// public-bus binding by accident — the auction service's audit events would
/// silently stop landing in the recorder's events table without these guards.
/// </summary>
public sealed class RecorderEventsBindingTests
{
    [Fact]
    public void Topology_DeclaresPublicEventsExchange()
    {
        Assert.Equal("bifrost.public", RecorderTopology.PublicEventsExchange);
        Assert.Equal("events.#", RecorderTopology.PublicEventsRoutingKey);
    }

    [Fact]
    public void Topology_LegacyExchangeAndKeysStillPresent()
    {
        // Legacy bindings preserved — keeps the door open for a future
        // direct order-lifecycle publisher to reintroduce traffic on the
        // existing trader-events topic without recorder changes.
        Assert.Equal("bifrost.events.v1", RecorderTopology.TraderEventsExchange);
        Assert.Equal("bifrost.recorder.events.v1", RecorderTopology.RecorderEventsQueue);
        Assert.Equal("order.#", RecorderTopology.OrderRoutingKey);
        Assert.Equal("lifecycle.#", RecorderTopology.LifecycleRoutingKey);
    }

    [Fact]
    public void RabbitMqRecorderConsumer_SourceContains_PublicEventsBinding()
    {
        // Source-level grep for the tokens we expect to appear in
        // RabbitMqRecorderConsumer.ExecuteAsync after the public-events
        // binding is added. The string "events.#" is the routing-key
        // constant value; the two PublicEvents* names are referenced
        // through the topology constants. This catches "we removed the
        // binding by accident" regressions even when the rest of the
        // build still succeeds.
        var sourcePath = FindSourceFile(
            Path.Combine("src", "recorder", "Infrastructure", "RabbitMqRecorderConsumer.cs"));
        var text = File.ReadAllText(sourcePath);
        Assert.Contains("PublicEventsExchange", text);
        Assert.Contains("PublicEventsRoutingKey", text);
        Assert.Contains("events.#", text);
    }

    [Fact]
    public void Topology_DeclaresMcAuditExchangeAndCommandRoutingKey()
    {
        // Phase 06 D-23: the recorder binds bifrost.mc.v1/mc.command.# into
        // the existing recorder queue so every orchestrator-published
        // McCommandLog envelope multiplexes into mc_commands without a
        // schema migration. Lock the constants — the orchestrator's
        // OrchestratorRabbitMqTopology.McAuditExchange must use the same
        // exchange name; the routing-key pattern is the wildcard subset of
        // the orchestrator's per-command snake_case routing keys.
        Assert.Equal("bifrost.mc.v1", RecorderTopology.McAuditExchange);
        Assert.Equal("mc.command.#", RecorderTopology.McCommandRoutingPattern);
    }

    [Fact]
    public void RabbitMqRecorderConsumer_SourceContains_McAuditBinding()
    {
        // Source-level grep that the recorder's ExecuteAsync wires the
        // bifrost.mc.v1 / mc.command.# binding alongside the three existing
        // bindings. This catches the regression where a future edit removes
        // the audit binding by accident — orchestrator-published McCommandLog
        // envelopes would silently stop landing in mc_commands without these
        // guards.
        var sourcePath = FindSourceFile(
            Path.Combine("src", "recorder", "Infrastructure", "RabbitMqRecorderConsumer.cs"));
        var text = File.ReadAllText(sourcePath);
        Assert.Contains("McAuditExchange", text);
        Assert.Contains("McCommandRoutingPattern", text);
        Assert.Contains("DispatchMcCommandLog", text);
    }

    /// <summary>
    /// Walks up from the test bin's <see cref="AppContext.BaseDirectory"/>
    /// until the requested relative path resolves. Avoids hard-coding the
    /// repo root so the test runs identically under <c>dotnet test</c>
    /// from the repo root, the test project directory, or CI.
    /// </summary>
    private static string FindSourceFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException($"Could not locate {relative}");
    }
}
