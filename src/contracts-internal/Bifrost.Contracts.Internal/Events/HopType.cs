namespace Bifrost.Contracts.Internal.Events;

public enum HopType
{
    Created,
    Accepted,
    Fill,
    Rejected,
    Cancelled,
    PricingSignalReceived,
    HostEventArrival,
    CallbackEntry,
    Submitted
}
