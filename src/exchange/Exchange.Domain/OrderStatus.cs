namespace Bifrost.Exchange.Domain;

public enum OrderStatus
{
    New,
    Active,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected
}