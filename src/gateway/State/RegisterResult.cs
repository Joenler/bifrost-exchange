namespace Bifrost.Gateway.State;

/// <summary>
/// Outcome of TeamRegistry.TryRegister. Maps directly to RegisterAck fields
/// {client_id, current_round_state, resumed_from_sequence, reregister_required}
/// in strategy.proto.
/// </summary>
public sealed record RegisterResult(
    bool Success,
    TeamState? TeamState,
    long ResumedFromSequence,
    bool ReregisterRequired,
    string? FailureDetail);
