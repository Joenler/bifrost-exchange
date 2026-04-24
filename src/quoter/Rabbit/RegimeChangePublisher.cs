using Bifrost.Contracts.Events;
using Bifrost.Contracts.Internal;
using Bifrost.Exchange.Infrastructure.RabbitMq;
using Bifrost.Quoter.Abstractions;
using Bifrost.Quoter.Schedule;
using QuoterRegime = Bifrost.Quoter.Schedule.Regime;
using ProtoRegime = Bifrost.Contracts.Events.Regime;

namespace Bifrost.Quoter.Rabbit;

/// <summary>
/// Live <see cref="IRegimeChangePublisher"/> backed by Phase 02
/// <see cref="BufferedEventPublisher"/>. Maps the quoter's internal
/// <see cref="QuoterRegime"/> enum onto the Phase 01 protobuf
/// <see cref="ProtoRegime"/> (the integer values are byte-identical -- both
/// enums lock the events.proto::Regime contract: Unspecified=0, Calm=1,
/// Trending=2, Volatile=3, Shock=4) and emits an
/// <c>events.proto::Event { regime_change: RegimeChange { from, to, mc_forced } }</c>
/// onto <c>RabbitMqTopology.PublicExchange</c> with routing key
/// <see cref="QuoterRabbitTopology.EventsRoutingKeyRegimeChange"/>.
///
/// The buffered publisher is non-blocking: <see cref="Emit"/> enqueues onto the
/// channel-backed publish queue and returns immediately, so the quoter's
/// regime-transition critical section never stalls on broker I/O.
/// </summary>
public sealed class RegimeChangePublisher(BufferedEventPublisher buffered) : IRegimeChangePublisher
{
    public void Emit(RegimeTransition transition)
    {
        var evt = new Event
        {
            // Quoter does not know the wire-side timestamp_ns budget; the
            // BufferedEventPublisher's underlying RabbitMqEventPublisher stamps
            // its own envelope timestamp. The Event.timestamp_ns slot is left
            // at the proto default (0) -- downstream consumers should treat
            // the envelope timestamp as the authoritative wall-clock anchor.
            TimestampNs = 0L,
            Severity = Severity.Info,
            RegimeChange = new RegimeChange
            {
                From = MapRegime(transition.From),
                To = MapRegime(transition.To),
                McForced = transition.McForced,
            },
        };

        // Enqueue on the buffered publisher -- non-blocking.
        _ = buffered.PublishPublicEvent(
            QuoterRabbitTopology.EventsRoutingKeyRegimeChange,
            MessageTypes.RegimeChange,
            evt);
    }

    private static ProtoRegime MapRegime(QuoterRegime r) => r switch
    {
        QuoterRegime.Unspecified => ProtoRegime.Unspecified,
        QuoterRegime.Calm => ProtoRegime.Calm,
        QuoterRegime.Trending => ProtoRegime.Trending,
        QuoterRegime.Volatile => ProtoRegime.Volatile,
        QuoterRegime.Shock => ProtoRegime.Shock,
        _ => ProtoRegime.Unspecified,
    };
}
