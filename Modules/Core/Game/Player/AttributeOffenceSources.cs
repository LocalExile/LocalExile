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
    ];

    // The table's rows (fx_per_attribute), worked out once for each load of the game data; they count only while the
    // effect rules are on.
    sealed record Table(object Rows, (uint Input, uint Output, int Attribute, int Group, double Scale)[] Rules);
    static Table _table = new(new object(), []);

    static (uint Input, uint Output, int Attribute, int Group, double Scale)[] FromTable()
    {
        var rows = GameData.FxPerAttribute;
        var cached = Volatile.Read(ref _table);
        if (ReferenceEquals(cached.Rows, rows)) return cached.Rules;
        var rules = new List<(uint, uint, int, int, double)>();
        int unknown = 0;
        foreach (var row in rows)
        {
            int attribute = row.Attribute switch { "strength" => 0, "dexterity" => 1, "intelligence" => 2, _ => -1 };
            if (attribute >= 0 && row.Every > 0 && Program.Stats.Key(row.Stat) is uint input && Program.Stats.Key(row.CountsAs) is uint output)
                rules.Add((input, output, attribute, row.Every, row.Scale));
            else unknown++;
        }
        if (rows.Count > 0)
            Console.WriteLine($"[rules] {rules.Count} per-attribute stat(s) from the fx tables" + (unknown > 0 ? $"; {unknown} left out" : ""));
        Volatile.Write(ref _table, cached = new(rows, [.. rules]));
        return cached.Rules;
    }

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
        if (StatConditions.UseTable)
            foreach (var (input, output, attribute, group, scale) in FromTable())
                if (sources.TryGetValue(input, out int value) && value != 0)
                    added[output] = added.GetValueOrDefault(output)
                        + (long)Math.Round(value * scale * (attributes[attribute] / group), MidpointRounding.AwayFromZero);
        foreach (var (key, value) in added)
            sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
    }
}
