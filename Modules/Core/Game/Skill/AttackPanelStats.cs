using GameServer.Core.World;


/// <summary>Selected attack-gem main-hand hit rows and critical-weighted averages using the actual action rate.</summary>
static class AttackPanelStats
{
    static readonly string[] Types = ["total", "physical", "fire", "cold", "lightning", "chaos"];

    public static List<(string Stat, int Value)> Compute(SkillHitPlan plan, DamagePacket packet, double attacksPerSecond,
        double damageFactor = 1)
    {
        if (plan.Kind != SkillHitKind.Attack) return [];
        var ranges = PanelStats.HandDamage(packet, "main_hand").ToDictionary(r => r.Stat, r => r.Value);
        var result = new List<(string, int)>();
        foreach (string type in Types)
            foreach (string bound in new[] { "minimum", "maximum" })
            {
                string stat = $"main_hand_{bound}_{type}_damage";
                result.Add((stat, ranges.GetValueOrDefault(stat))); // Clear inherited types absent after conversion.
            }
        var (perUse, perSecond) = SpellDamage.Display((packet.MinTotal, packet.MaxTotal),
            attacksPerSecond, damageFactor);
        result.Add(("display_hundred_times_damage_per_skill_use", perUse));
        result.Add(("hundred_times_damage_per_second", perSecond));
        return result;
    }
}
