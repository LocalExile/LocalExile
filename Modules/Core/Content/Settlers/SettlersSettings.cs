namespace GameServer.Modules.Settlers;

/// <summary>
/// Waypoint-tunable settings for Kingsmarch / Settlers of Kalguur (<c>settings.json</c>).
/// All defaults match 3.29.3c retail behaviour (1.0x multipliers, offline catch-up enabled).
/// </summary>
public sealed record SettlersSettings(
    double GoldBurnRateMultiplier = 1.0,
    double WageMultiplier = 1.0,
    double MiningSpeedMultiplier = 1.0,
    double SmeltingSpeedMultiplier = 1.0,
    double FarmingSpeedMultiplier = 1.0,
    double CropYieldMultiplier = 1.0,
    double DisenchantingSpeedMultiplier = 1.0,
    double DustYieldMultiplier = 1.0,
    double ShippingSpeedMultiplier = 1.0,
    double ShippingRiskMultiplier = 1.0,
    double ShippingRewardMultiplier = 1.0,
    double MappingSpeedMultiplier = 1.0,
    double MappingDeathChanceMultiplier = 1.0,
    double OreFoundMultiplier = 1.0,
    double BossAmbushChanceMultiplier = 1.0,
    bool OfflineCatchupEnabled = true)
{
    public static readonly SettlersSettings Default = new();
}
