using Bifrost.Quoter.Schedule;

namespace Bifrost.Quoter.Abstractions;

/// <summary>
/// Narrow seam for emitting regime-change events out of the quoter. The
/// production implementation bridges onto the buffered RabbitMQ event
/// publisher; the build-green default <c>NoOpRegimeChangePublisher</c> only
/// logs so the schedule wiring can be exercised without a broker connection.
/// </summary>
public interface IRegimeChangePublisher
{
    /// <summary>
    /// Emit a regime transition. Called from inside the quoter's regime-change
    /// critical section before any cancel-all is issued so observers see the
    /// transition in causal order.
    /// </summary>
    void Emit(RegimeTransition transition);
}
