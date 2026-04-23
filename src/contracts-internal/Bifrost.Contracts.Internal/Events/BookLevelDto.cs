namespace Bifrost.Contracts.Internal.Events;

/// <summary>
/// Single price level in the order book with price, quantity, and order count.
/// </summary>
public sealed record BookLevelDto(
    long PriceTicks,
    decimal Quantity,
    int OrderCount);
