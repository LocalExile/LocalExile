using GameServer.Core.World;


/// <summary>Per-event roll policies read from actual passive stats. These do not change the character's ordinary
/// resistance caps or turn a random chance into an average bonus. The caller rolls them once for each hit.</summary>
readonly record struct GamblerModifiers(int DamageExtraRolls = 0, int EnemyDamageExtraRolls = 0,
    int TripleDamageChance = 0, int DamageMorePct = 0, int InvertElementalResistanceChance = 0, int EnemyHitsNinetyResistanceChance = 0,
    int DoubleDamageChance = 0, int SpellDoubleDamageChance = 0)
{
    public static GamblerModifiers From(Func<string, int> get) => new(
        get("base_extra_damage_rolls"), get("base_enemy_extra_damage_rolls"),
        get("base_chance_to_deal_triple_damage_%"), get("marauder_hidden_ascendancy_damage_+%_final"),
        get("treat_enemy_resistances_as_negated_on_elemental_damage_hit_%_chance"),
        get("enemy_hits_%_chance_to_treat_elemental_resistances_as_90%"),
        get("chance_to_deal_double_damage_%"), get("spell_chance_to_deal_double_damage_%"));

    /// <summary>One skill's hit: the skill's own chance to deal double damage (Heavy Strike's gem) adds to the player's,
    /// and a spell adds spell_chance_to_deal_double_damage_%.</summary>
    public GamblerModifiers ForSkill(Func<string, int> skill, bool spell) => this with
    {
        DoubleDamageChance = DoubleDamageChance + skill("chance_to_deal_double_damage_%") + (spell ? SpellDoubleDamageChance : 0),
    };

    /// <summary>Growing Accumulator's less damage applies to hits and damage over time; triple damage only to hits.</summary>
    public bool InvertsResistance(Random random) => RandomRolls.Chance(InvertElementalResistanceChance, 100, random);
    public bool UsesNinetyResistance(Random random) => RandomRolls.Chance(EnemyHitsNinetyResistanceChance, 100, random);

    public double DamageFactor => Math.Max(0, (100.0 + DamageMorePct) / 100.0);

    /// <summary>Apply the less damage and roll the triple-damage chance, then the double-damage one, then roll each damage
    /// type independently. Critical strike multiplication and target mitigation still belong to the caller.</summary>
    public DamagePacket RollOutgoingHit(DamagePacket hit, Random random)
    {
        // Triple damage overrides double (PoB CalcOffence: having both is the same as triple), so double only rolls when
        // triple misses.
        int multiplier = RandomRolls.Chance(TripleDamageChance, 100, random) ? 3
            : RandomRolls.Chance(DoubleDamageChance, 100, random) ? 2 : 1;
        return hit.Scale(DamageFactor * multiplier).Roll(random, DamageExtraRolls);
    }

    /// <summary>Overly Confident changes enemy HIT damage rolls, never damage-over-time ticks.</summary>
    public DamagePacket RollIncomingHit(DamagePacket hit, Random random) => hit.Roll(random, EnemyDamageExtraRolls);

    /// <summary>The inversion is a single outcome for this hit. Cap the actual resistance first, then invert it;
    /// recapping the inverted value would incorrectly help targets with negative resistance.</summary>
    public DamagePacket MitigateOutgoingHit(DamagePacket hit, Func<DamageType, DamageModel.ResistanceValue> resistance, Random random)
    {
        bool invert = RandomRolls.Chance(InvertElementalResistanceChance, 100, random);
        return hit.Select((type, range) =>
        {
            if (type == DamageType.Physical) return range;
            var r = resistance(type);
            int effective = DamageModel.Capped(r.Value, r.Maximum);
            if (invert && DamageTypes.IsElemental(type)) effective = -effective;
            effective -= r.Penetration;
            return new(DamageModel.MitigateBy(range.Min, effective), DamageModel.MitigateBy(range.Max, effective));
        });
    }

    /// <summary>Risky Exploit overrides elemental resistance to exactly 90% for this enemy hit, even if the ordinary
    /// maximum is 75%. Chaos/physical damage and damage over time retain their usual rules.</summary>
    public DamagePacket MitigateIncomingHit(DamagePacket hit, Func<DamageType, DamageModel.ResistanceValue> resistance, Random random)
    {
        bool ninety = RandomRolls.Chance(EnemyHitsNinetyResistanceChance, 100, random);
        return hit.Select((type, range) =>
        {
            if (type == DamageType.Physical) return range; // physical armour/reduction is a separate caller stage
            var r = ninety && DamageTypes.IsElemental(type) ? new DamageModel.ResistanceValue(90, 90) : resistance(type);
            return new(DamageModel.Mitigate(range.Min, r.Value, r.Maximum), DamageModel.Mitigate(range.Max, r.Value, r.Maximum));
        });
    }
}
