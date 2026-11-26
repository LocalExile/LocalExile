/// <summary>Qualified global coefficients use complete groups of final owner attributes.</summary>
public static class AttributeOffenceSources
{
    static readonly (uint Input, uint Output, int Attribute, int Group)[] Rules =
    [
        (Stat.AreaOfEffectPlusPctPer20Int, Stat.BaseSkillAreaOfEffectPlusPct, 2, 20),
        (Stat.AreaOfEffectPlusPctPer50Strength, Stat.BaseSkillAreaOfEffectPlusPct, 0, 50),
        (Stat.AttackSpeedPlusPctPer10Dex, Stat.AttackSpeedPlusPct, 1, 10),
        (Stat.AttackSpeedPlusPctPer25Dex, Stat.AttackSpeedPlusPct, 1, 25),
        (Stat.CriticalStrikeChancePlusPctPer10Strength, Stat.CriticalStrikeChancePlusPct, 0, 10),
        (Stat.CriticalStrikeChancePlusPctPer25Intelligence, Stat.CriticalStrikeChancePlusPct, 2, 25),
        (Stat.DamagePlusPctPer15Strength, Stat.DamagePlusPct, 0, 15),
        (Stat.DamagePlusPctPer15Dex, Stat.DamagePlusPct, 1, 15),
        (Stat.DamagePlusPctPer15Int, Stat.DamagePlusPct, 2, 15),
        (Stat.DamagePlusPctPer100Strength, Stat.DamagePlusPct, 0, 100),
        (Stat.DamagePlusPctPer100Dexterity, Stat.DamagePlusPct, 1, 100),
        (Stat.DamagePlusPctPer100Intelligence, Stat.DamagePlusPct, 2, 100),
        (Stat.ElementalDamagePlusPctPer10Dexterity, Stat.ElementalDamagePlusPct, 1, 10),
        (Stat.FireDamagePlusPctPer20Strength, Stat.FireDamagePlusPct, 0, 20),
        (Stat.SpellDamagePlusPctPer10Strength, Stat.SpellDamagePlusPct, 0, 10),
        (Stat.SpellDamagePlusPctPer10Int, Stat.SpellDamagePlusPct, 2, 10),
        (Stat.SpellDamagePlusPctPer16Strength, Stat.SpellDamagePlusPct, 0, 16),
        (Stat.SpellDamagePlusPctPer16Dex, Stat.SpellDamagePlusPct, 1, 16),
        (Stat.SpellDamagePlusPctPer16Int, Stat.SpellDamagePlusPct, 2, 16),
        (Stat.XAccuracyPer2Intelligence, Stat.AccuracyRating, 2, 2),
        (Stat.BleedDurationPer12IntelligencePlusPct, Stat.BleedDurationPlusPct, 2, 12),
        (Stat.PhysicalDamageOverTimePer10DexterityPlusPct, Stat.PhysicalDamageOverTimePlusPct, 1, 10),
        (Stat.ProjectileDamagePlusPctPer16Dexterity, Stat.ProjectileDamagePlusPct, 1, 16),
    ];

    // Apply once to a fresh qualified source sum, before pools, skill snapshots and panels consume it.
    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats,
        int strength, int dexterity, int intelligence)
    {
        int[] attributes = [Math.Max(0, strength), Math.Max(0, dexterity), Math.Max(0, intelligence)];
        Dictionary<uint, long> added = [];
        foreach (var (input, output, attribute, group) in Rules)
            if (input is var coefficient && sources.TryGetValue(coefficient, out int value)
                && value != 0 && output is var target)
                added[target] = added.GetValueOrDefault(target) + (long)value * (attributes[attribute] / group);
        foreach (var (key, value) in added)
            sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
    }
}
