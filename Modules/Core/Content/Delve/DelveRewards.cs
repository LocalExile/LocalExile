namespace GameServer.Modules.Delve;

/// <summary>Server policy for a completed node. The tutorial's physical depth 2 is the first rewarded depth.</summary>
sealed record DelveRewards(int Minimum, int Maximum, double QuantityPerDepth, double RarityPerDepth, int Cap)
{
    public static DelveRewards Current()
    {
        var settings = SystemSettings.Default;
        int min = Math.Clamp(settings.GetInt("delve_reward_min_items"), 1, 2000);
        int max = Math.Clamp(settings.GetInt("delve_reward_max_items"), min, 2000);
        return new(min, max, Math.Clamp(settings.Get("delve_reward_quantity_per_depth_pct"), 0, 1000),
            Math.Clamp(settings.Get("delve_reward_rarity_per_depth_pct"), 0, 1000),
            Math.Clamp(settings.GetInt("delve_reward_item_cap"), 1, 2000));
    }

    public (int Count, int Rarity, double QuantityPercent) Roll(int depth, Random rng)
    {
        int tiers = Math.Max(1, depth - 1);
        double quantity = Math.Min(100000, tiers * QuantityPerDepth);
        int rarity = (int)Math.Min(100000, tiers * RarityPerDepth);
        double count = Math.Min(Cap, rng.Next(Minimum, Maximum + 1) * (1 + quantity / 100));
        int whole = (int)Math.Floor(count);
        return (Math.Min(Cap, whole + (rng.NextDouble() < count - whole ? 1 : 0)), rarity, quantity);
    }
}
