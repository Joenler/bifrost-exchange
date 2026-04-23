namespace Bifrost.Exchange.Application.RoundState;

/// <summary>
/// Production static-value implementation of <see cref="IRoundStateSource"/>. Reads the
/// round's initial state from <c>appsettings.json</c> (<c>RoundState:Initial</c>) at
/// bootstrap and never fires <see cref="OnChange"/>. The future RabbitMQ-backed source
/// swaps in later when the orchestrator begins publishing round transitions.
/// </summary>
public sealed class ConfigRoundStateSource : IRoundStateSource
{
    public RoundState Current { get; }

    // CS0067 suppression: OnChange is part of the IRoundStateSource contract but is never
    // raised in this production static-value implementation. The test-only
    // InMemoryRoundStateSource and the future RabbitMQ-backed source fire it.
#pragma warning disable CS0067
    public event EventHandler<RoundStateChangedEventArgs>? OnChange;
#pragma warning restore CS0067

    public ConfigRoundStateSource(RoundState initial)
    {
        Current = initial;
    }
}
