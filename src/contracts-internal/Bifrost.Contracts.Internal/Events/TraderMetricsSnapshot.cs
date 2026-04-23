namespace Bifrost.Contracts.Internal.Events;

public sealed record TraderMetricsSnapshot(
    long TimestampNs,
    FrameworkMetrics Framework,
    StrategyMetrics[] Strategies);

public sealed record FrameworkMetrics(
    decimal TotalPnl,
    decimal NetRealized,
    decimal UnrealizedPnl,
    decimal TotalFees,
    int FillCount,
    decimal PassiveRatio,
    decimal AggressiveRatio,
    decimal CompletionRatio,
    decimal TtdExposure,
    double? OtrRatio,
    PositionEntry[] Positions,
    decimal DrawdownPct = 0m,
    decimal MaxDrawdownPct = 0m,
    decimal BiggestWinPnl = 0m,
    string? BiggestWinInstrument = null,
    decimal BiggestLossPnl = 0m,
    string? BiggestLossInstrument = null,
    decimal MakerFees = 0m,
    decimal TakerFees = 0m,
    AreaPnlEntry[]? PnlByArea = null,
    int CancelCount = 0);

public sealed record StrategyMetrics(
    string StrategyName,
    string ClientId,
    string DisplayName,
    decimal TotalPnl,
    decimal NetRealized,
    decimal UnrealizedPnl,
    decimal TotalFees,
    int FillCount,
    decimal PassiveRatio,
    decimal AggressiveRatio,
    decimal CompletionRatio,
    decimal TtdExposure,
    double? OtrRatio,
    bool IsStale,
    PositionEntry[] Positions,
    decimal DrawdownPct = 0m,
    decimal MaxDrawdownPct = 0m,
    double HitRate = 0.0,
    string Status = "Running",
    int CrashCount = 0,
    string? LastCrashMessage = null,
    int Cancels = 0,
    string StrategyType = "financial");

public sealed record PositionEntry(
    string InstrumentId,
    decimal NetQty,
    decimal UnrealizedPnl,
    decimal TtdWeight);

public sealed record AreaPnlEntry(
    string Area,
    decimal NetRealized);

public sealed record DahPositionEntry(string AgentId, string InstrumentId, decimal Quantity);

public sealed record DahClearingPriceEntry(string InstrumentId, long PriceTicks);

public sealed record DahPositionsSnapshot(
    DahPositionEntry[] Positions,
    DahClearingPriceEntry[]? ClearingPrices = null,
    ForecastBaselineEntry[]? ForecastBaselines = null);

public sealed record ForecastBaselineEntry(
    string InstrumentId,
    double WindMw,
    double SolarMw,
    double DemandMw,
    double ResidualLoadMw);

public sealed record ForecastSnapshotEntry(
    string InstrumentId,
    double WindMw,
    double SolarMw,
    double DemandMw,
    double ResidualLoadMw);

public sealed record StrategyForecastSnapshot(
    string ClientId,
    string StrategyName,
    ForecastSnapshotEntry[] Forecasts);

public sealed record FairValueEntry(string InstrumentId, long FairValueTicks);

public sealed record FairValueSnapshot(FairValueEntry[] Values);
