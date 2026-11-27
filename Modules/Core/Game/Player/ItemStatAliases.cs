/// <summary>Item stats whose effect is other stats', under a second id.</summary>
public static class ItemStatAliases
{
    static readonly (uint Input, uint[] Outputs)[] Rules =
    [
        // "Adds # to # Cold Damage to Spells and Attacks": the attack and the spell added damage, not the global one,
        // which secondary damage, heralds and Madness explosions also read.
        (Stat.SpellAndAttackMinimumAddedColdDamage, [Stat.AttackMinimumAddedColdDamage, Stat.SpellMinimumAddedColdDamage]),
        (Stat.SpellAndAttackMaximumAddedColdDamage, [Stat.AttackMaximumAddedColdDamage, Stat.SpellMaximumAddedColdDamage]),
        (Stat.SpellAndAttackMinimumAddedFireDamage, [Stat.AttackMinimumAddedFireDamage, Stat.SpellMinimumAddedFireDamage]),
        (Stat.SpellAndAttackMaximumAddedFireDamage, [Stat.AttackMaximumAddedFireDamage, Stat.SpellMaximumAddedFireDamage]),
        (Stat.SpellAndAttackMinimumAddedLightningDamage,
            [Stat.AttackMinimumAddedLightningDamage, Stat.SpellMinimumAddedLightningDamage]),
        (Stat.SpellAndAttackMaximumAddedLightningDamage,
            [Stat.AttackMaximumAddedLightningDamage, Stat.SpellMaximumAddedLightningDamage]),
        (Stat.DamagePlusPctVsBlindedEnemies, [PlayerHitDamage.VsBlindedDamageStat]),
    ];

    // The table's rows (fx_aliases), worked out once for each load of the game data. They count only while the
    // effect rules are on, so a server without them reads what the list above gives and nothing else.
    sealed record Table(object Rows, (uint Input, uint Output, double Scale)[] Aliases);
    static Table _table = new(new object(), []);

    static (uint Input, uint Output, double Scale)[] FromTable()
    {
        var rows = GameData.FxAliases;
        var cached = Volatile.Read(ref _table);
        if (ReferenceEquals(cached.Rows, rows)) return cached.Aliases;
        var aliases = new List<(uint, uint, double)>();
        int unknown = 0;
        foreach (var row in rows)
            if (Program.Stats.Key(row.Stat) is uint input && Program.Stats.Key(row.CountsAs) is uint output && input != output) aliases.Add((input, output, row.Scale));
            else unknown++;
        if (rows.Count > 0)
            Console.WriteLine($"[rules] {aliases.Count} stat alias(es) from the fx tables" + (unknown > 0 ? $"; {unknown} left out (a stat this build does not have)" : ""));
        Volatile.Write(ref _table, cached = new(rows, [.. aliases]));
        return cached.Aliases;
    }

    // Apply once to a fresh qualified source sum, before pools, skill snapshots and panels consume it.
    public static void Apply(Dictionary<uint, int> sources)
    {
        foreach (var (input, outputs) in Rules)
            if (sources.TryGetValue(input, out int value) && value != 0)
                foreach (uint output in outputs)
                    sources[output] = ActorScale.Saturate((long)sources.GetValueOrDefault(output) + value);
        if (!StatConditions.UseTable) return;
        foreach (var (input, output, scale) in FromTable())
            if (sources.TryGetValue(input, out int value) && value != 0)
                sources[output] = ActorScale.Saturate((long)sources.GetValueOrDefault(output)
                    + (scale == 1 ? value : (long)Math.Round(value * scale, MidpointRounding.AwayFromZero)));
    }
}
