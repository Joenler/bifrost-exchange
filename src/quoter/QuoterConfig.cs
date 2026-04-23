namespace Bifrost.Quoter;

/// <summary>
/// Options record bound from the <c>Quoter:*</c> section of <c>appsettings.json</c>.
/// All defaults match the values committed in the sibling <c>appsettings.json</c>;
/// runtime overrides come from environment variables or container-level config.
/// </summary>
public sealed class QuoterConfig
{
    public string ScenarioPath { get; init; } = "/scenarios/calm-drift.json";

    public int GbmStepMs { get; init; } = 500;

    public long MockTruthPriceTicks { get; init; } = 5000;

    public decimal MaxNetPosition { get; init; } = 100m;

    public decimal HardCapRelease { get; init; } = 80m;

    public decimal BaseQuantity { get; init; } = 2.0m;

    public int RequoteThresholdTicks { get; init; } = 5;

    /// <summary>γ inventory risk aversion (config-global).</summary>
    public double InventoryRiskAversion { get; init; } = 0.1;

    /// <summary>Book-blend weight w applied to the truth/microprice mix; 1.0 falls back to pure truth.</summary>
    public double BookBlendWeight { get; init; } = 0.8;

    public double[] LevelSpacingMultipliers { get; init; } = new[] { 1.0, 1.5, 2.5 };

    public double[] LevelQuantityFractions { get; init; } = new[] { 0.5, 0.3, 0.2 };
}
