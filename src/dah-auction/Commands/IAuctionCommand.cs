namespace Bifrost.DahAuction.Commands;

/// <summary>
/// Marker interface for commands consumed by the DAH auction write-loop
/// (<c>Bifrost.DahAuction.State.AuctionWriteLoop</c>). The loop
/// drains a bounded <see cref="System.Threading.Channels.Channel{T}"/> of
/// <see cref="IAuctionCommand"/> and switches on concrete record type.
/// Every mutation to the in-memory (team, quarter) -&gt; BidMatrix map happens
/// inside the loop's single-writer drain body — HTTP handlers, OnChange
/// handlers, and the RabbitMQ consumer never touch state directly.
/// </summary>
public interface IAuctionCommand { }
