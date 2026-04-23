namespace Bifrost.Exchange.Application.RoundState;

/// <summary>
/// BIFROST-invented seam supplying the current <see cref="RoundState"/> to the
/// <see cref="OrderValidator"/>. Two implementations ship in this phase:
/// <c>ConfigRoundStateSource</c> (production; static value from appsettings) and
/// <c>InMemoryRoundStateSource</c> in the tests project (mutable with
/// <see cref="OnChange"/> firing). A future <c>RabbitMqRoundStateSource</c> will
/// subscribe to the <c>bifrost.round</c> topic when the orchestrator ships.
/// </summary>
public interface IRoundStateSource
{
    RoundState Current { get; }

    event EventHandler<RoundStateChangedEventArgs> OnChange;
}

/// <summary>
/// Payload fired by <see cref="IRoundStateSource.OnChange"/> when a round transitions.
/// <paramref name="TimestampNs"/> is nanoseconds since Unix epoch.
/// </summary>
public sealed record RoundStateChangedEventArgs(RoundState Previous, RoundState Current, long TimestampNs);
