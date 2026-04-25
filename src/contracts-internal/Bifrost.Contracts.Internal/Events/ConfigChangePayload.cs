namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Wire DTO for publications on bifrost.events.v1/events.config_change. The
/// orchestrator is the publisher (emits on ConfigSetCmd receipt); downstream
/// consumers (quoter, imbalance, gateway) re-read their live-tune state in
/// their respective phases. All fields are free-form strings because
/// ConfigSetCmd carries opaque path+value.
/// </summary>
public sealed record ConfigChangePayload(
    string Path,
    string OldValue,
    string NewValue);
