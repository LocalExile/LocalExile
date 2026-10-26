using GameServer.Core.World;

/// <summary>PR #198's selected attack-gem main-hand hit rows and pre-crit averages using the emitted native APS.
/// This does not include accuracy, critical weighting or target mitigation.</summary>
static class AttackPanelStats
{
    static readonly string[] Types = ["total", "physical", "fire", "cold", "lightning", "chaos"];

    public static List<(string Stat, int Value)> Compute(SkillHitPlan plan, DamagePacket packet, int hundredTimesAttacksPerSecond)
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
            Math.Max(0, hundredTimesAttacksPerSecond) / 100.0);
        result.Add(("display_hundred_times_damage_per_skill_use", perUse));
        result.Add(("hundred_times_damage_per_second", perSecond));
        return result;
    }
}
