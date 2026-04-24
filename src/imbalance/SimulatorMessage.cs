using Bifrost.Exchange.Application.RoundState;

namespace Bifrost.Imbalance;

/// <summary>
/// Discriminated union for messages drained by the simulator actor loop. Every producer
/// (fill consumer, shock consumer, forecast timer, round-state bridge) pushes one of these
/// onto the shared bounded channel; the single drain loop pattern-matches on type and is
/// the sole mutator of simulator state.
/// </summary>
public abstract record SimulatorMessage(long TsNs);

/// <summary>
/// Fill event consumed from the exchange private topic. Only quarter fills flow here;
/// the fill consumer drops hour-instrument fills at the boundary via QuarterIndexResolver.
/// </summary>
public sealed record FillMessage(
    long TsNs,
    string ClientId,
    string InstrumentId,
    int QuarterIndex,
    string Side,
    long QuantityTicks) : SimulatorMessage(TsNs);

/// <summary>
/// MC-injected physical shock. QuarterIndex is required (enforced at the orchestrator
/// boundary; the simulator asserts it on ingress). Round-persistent shocks commit directly
/// into A_physical; transient shocks are tracked with an expiry window and roll off.
/// </summary>
public sealed record ShockMessage(
    long TsNs,
    int Mw,
    string Label,
    ShockPersistence Persistence,
    int QuarterIndex) : SimulatorMessage(TsNs);

/// <summary>
/// Forecast cadence tick — drives one ForecastUpdate publication per tick during RoundOpen.
/// Emitted by the forecast timer hosted service on a PeriodicTimer wired through the
/// injected TimeProvider so tests can advance virtual time.
/// </summary>
public sealed record ForecastTickMessage(long TsNs) : SimulatorMessage(TsNs);

/// <summary>
/// Round-state transition from the IRoundStateSource bridge. The drain loop uses this to
/// reset per-round accumulators on IterationOpen, compute gate prints at Gate, emit
/// settlements at Settled, and gate all other processing on RoundOpen.
/// </summary>
public sealed record RoundStateMessage(
    RoundState Previous,
    RoundState Current,
    long TsNs) : SimulatorMessage(TsNs);
