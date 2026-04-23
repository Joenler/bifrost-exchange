namespace Bifrost.Exchange.Domain;

public sealed record IcebergRefresh(
    OrderId OrderId,
    Quantity NewDisplayedQuantity,
    SequenceNumber NewPriority);