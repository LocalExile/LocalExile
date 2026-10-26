using GameServer.Core.World;

/// <summary>Selected spell hit rows: whole-number ranges and hundredths for pre-crit average damage and DPS.</summary>
static class SpellPanelStats
{
    static readonly string[] Types = ["total", "physical", "fire", "cold", "lightning", "chaos"];

    public static List<(string Stat, int Value)> Compute(SkillHitPlan plan, DamagePacket packet, int hundredTimesCastsPerSecond)
    {
        // Attack and unsupported action rows retain their existing weapon display path.
        if (plan.Kind != SkillHitKind.Spell) return [];
        if (!plan.CanHit) packet = DamagePacket.Empty;
        var ranges = PanelStats.HandDamage(packet, "spell").ToDictionary(r => r.Stat, r => r.Value);
        var result = new List<(string, int)>();
        foreach (string type in Types)
            foreach (string bound in new[] { "minimum", "maximum" })
            {
                string stat = $"spell_{bound}_{type}_damage";
                // Explicit zeros also replace inherited rows, including damage types absent from this packet.
                result.Add((stat, ranges.GetValueOrDefault(stat)));
            }
        var (perUse, perSecond) = SpellDamage.Display((packet.MinTotal, packet.MaxTotal),
            Math.Max(0, hundredTimesCastsPerSecond) / 100.0);
        result.Add(("display_hundred_times_damage_per_skill_use", perUse));
        result.Add(("hundred_times_damage_per_second", perSecond));
        return result;
    }
}
