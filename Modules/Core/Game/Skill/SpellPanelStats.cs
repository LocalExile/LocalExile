using GameServer.Core.World;


/// <summary>Selected spell hit rows: noncritical ranges and hundredths for critical-weighted average damage and DPS.</summary>
public static class SpellPanelStats
{
    static readonly string[] Types = ["total", "physical", "fire", "cold", "lightning", "chaos"];

    public static List<(uint Stat, int Value)> Compute(SkillHitPlan plan, DamagePacket packet,
        double castsPerSecond, bool includeMainHand = false, double damageFactor = 1)
    {
        // Attack and unsupported action rows retain their existing weapon display path.
        if (plan.Kind != SkillHitKind.Spell) return [];
        if (!plan.CanHit) packet = DamagePacket.Empty;
        var ranges = PanelStats.HandDamage(packet, "spell").ToDictionary(r => r.Stat, r => r.Value);
        var result = new List<(uint, int)>();
        foreach (string hand in includeMainHand ? new[] { "spell", "main_hand" } : ["spell"])
            foreach (string type in Types)
                foreach (string bound in new[] { "minimum", "maximum" })
                {
                    // Clear inherited weapon rows too when publishing a selected spell's final packet.
                    int value = ranges.GetValueOrDefault(Stat.Of($"spell_{bound}_{type}_damage"));
                    result.Add((Stat.Of($"{hand}_{bound}_{type}_damage"), value));
                }
        var (perUse, perSecond) = SpellDamage.Display((packet.MinTotal, packet.MaxTotal),
            castsPerSecond, damageFactor);
        result.Add((Stat.DisplayHundredTimesDamagePerSkillUse, perUse));
        result.Add((Stat.HundredTimesDamagePerSecond, perSecond));
        return result;
    }
}
