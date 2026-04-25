namespace Bifrost.Contracts.Internal;

/// <summary>
/// String constants for message type discrimination in envelope deserialization.
/// </summary>
public static class MessageTypes
{
    // Commands
    public const string SubmitOrder = "SubmitOrder";
    public const string CancelOrder = "CancelOrder";
    public const string ReplaceOrder = "ReplaceOrder";

    // Events
    public const string OrderAccepted = "OrderAccepted";
    public const string OrderRejected = "OrderRejected";
    public const string OrderCancelled = "OrderCancelled";
    public const string OrderExecuted = "OrderExecuted";
    public const string MarketOrderRemainderCancelled = "MarketOrderRemainderCancelled";

    public const string InstrumentList = "InstrumentList";
    public const string BookSnapshot = "BookSnapshot";

    public const string BookDelta = "BookDelta";
    public const string InstrumentAvailable = "InstrumentAvailable";
    public const string ExchangeMetadata = "ExchangeMetadata";
    public const string PublicTrade = "PublicTrade";
    public const string TraderMetrics = "TraderMetrics";
    public const string PublicOrderStats = "PublicOrderStats";
    public const string TraderOrderEvent = "TraderOrderEvent";
    public const string TraderLifecycleEvent = "TraderLifecycleEvent";
    public const string DahPositions = "DahPositions";
    public const string ForecastSnapshot = "ForecastSnapshot";
    public const string FairValue = "FairValue";

    // Public events bus payloads (events.proto::Event oneof variants).
    public const string RegimeChange = "RegimeChange";

    public const string ForecastUpdate = "ForecastUpdate";
    public const string ForecastRevision = "ForecastRevision";
    public const string PhysicalShock = "PhysicalShock";
    public const string ImbalanceSettlement = "ImbalanceSettlement";
    public const string ImbalancePrint = "ImbalancePrint";

    // Auction surface payloads.
    public const string AuctionBidSubmitted = "AuctionBidSubmitted";
    public const string AuctionClearingResult = "AuctionClearingResult";
    public const string AuctionNoCross = "AuctionNoCross";

    // Round orchestrator surfaces.
    public const string RoundStateChanged = "RoundStateChanged";
    public const string News = "News";
    public const string MarketAlert = "MarketAlert";
    public const string ConfigChange = "ConfigChange";
    public const string McCommandLog = "McCommandLog";
}
