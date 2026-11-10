using GameServer.Core.World;

/// <summary>
/// Volatility Support: "Supported Skills have (69)% less Minimum Attack Damage and (58)% more Maximum Attack Damage"
/// (minimum_attack_damage_+%_final_from_volatility_support and maximum_attack_damage_+%_final_from_volatility_support).
/// Applied to the attack's damage range before the hit rolls, so the panel and the hit use the same range.
/// </summary>
public static class Volatility
{
    public static DamagePacket Apply(DamagePacket hit, Func<string, int> get)
    {
        int minimum = get("minimum_attack_damage_+%_final_from_volatility_support");
        int maximum = get("maximum_attack_damage_+%_final_from_volatility_support");
        if (minimum == 0 && maximum == 0) return hit;
        double low = Math.Max(0, 1 + minimum / 100.0), high = Math.Max(0, 1 + maximum / 100.0);
        return hit.Select((type, range) =>
        {
            double min = Math.Max(0, range.Min * low), max = Math.Max(0, range.Max * high);
            return new DamageRange(Math.Min(min, max), Math.Max(min, max));
        });
    }
}
