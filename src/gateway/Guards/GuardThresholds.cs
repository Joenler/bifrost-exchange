using System.Text.Json;

namespace Bifrost.Gateway.Guards;

/// <summary>
/// ADR-0004 thresholds. Loaded once at startup from <c>config/guards.json</c> and held in DI.
///
/// ConfigSet mid-round is logged-but-deferred per ADR-0004 — re-loaded only on
/// <c>IterationOpen</c> transitions, NOT on the receiving command. Plan 05 wires the
/// re-load hook; this POCO is immutable so a frozen snapshot is what the GuardChain
/// evaluates against during a round.
/// </summary>
public sealed record GuardThresholds(
    int OtrWindowSeconds,
    int OtrMaxRatio,
    int OtrTimeoutSeconds,
    int MaxOpenOrdersPerInstrument,
    int MaxOrderNotionalMwh,
    int MaxPositionPerInstrumentMwh,
    string SelfTradeProtection,
    int GatewayMsgRatePerTeam,
    int GatewayMsgRateTimeoutSeconds)
{
    /// <summary>ADR-0004 §Configuration shape default values.</summary>
    public static GuardThresholds Defaults() => new(
        OtrWindowSeconds: 60,
        OtrMaxRatio: 50,
        OtrTimeoutSeconds: 1,
        MaxOpenOrdersPerInstrument: 50,
        MaxOrderNotionalMwh: 50,
        MaxPositionPerInstrumentMwh: 1000,
        SelfTradeProtection: "cancel_newer",
        GatewayMsgRatePerTeam: 500,
        GatewayMsgRateTimeoutSeconds: 1);

    /// <summary>
    /// Loads the snake_case JSON shape from ADR-0004 §Configuration shape. If the file is
    /// missing returns <see cref="Defaults"/> — the gateway boots with safe defaults so
    /// dev workflows don't require the config file to be present.
    /// </summary>
    public static GuardThresholds LoadFromFile(string path)
    {
        if (!File.Exists(path)) return Defaults();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var otr = root.GetProperty("otr");
        return new GuardThresholds(
            OtrWindowSeconds: otr.GetProperty("window_seconds").GetInt32(),
            OtrMaxRatio: otr.GetProperty("max_ratio").GetInt32(),
            OtrTimeoutSeconds: otr.GetProperty("timeout_seconds").GetInt32(),
            MaxOpenOrdersPerInstrument: root.GetProperty("max_open_orders_per_instrument").GetInt32(),
            MaxOrderNotionalMwh: root.GetProperty("max_order_notional_mwh").GetInt32(),
            MaxPositionPerInstrumentMwh: root.GetProperty("max_position_per_instrument_mwh").GetInt32(),
            SelfTradeProtection: root.GetProperty("self_trade_protection").GetString() ?? "cancel_newer",
            GatewayMsgRatePerTeam: root.GetProperty("gateway_msg_rate_per_team").GetInt32(),
            GatewayMsgRateTimeoutSeconds: root.GetProperty("gateway_msg_rate_timeout_seconds").GetInt32());
    }
}
