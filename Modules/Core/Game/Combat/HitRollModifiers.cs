using GameServer.Core.World;


/// <summary>Per-event roll policies read from actual passive stats. These do not change the character's ordinary
/// resistance caps or turn a random chance into an average bonus. The caller rolls them once for each hit.</summary>
public readonly record struct HitRollModifiers(int DamageExtraRolls = 0, int EnemyDamageExtraRolls = 0,
    int TripleDamageChance = 0, int DamageMorePct = 0, int InvertElementalResistanceChance = 0, int EnemyHitsNinetyResistanceChance = 0,
    int DoubleDamageChance = 0, int SpellDoubleDamageChance = 0)
{
    public static HitRollModifiers From(Func<uint, int> get) => new(
        get(Stat.BaseExtraDamageRolls), get(Stat.BaseEnemyExtraDamageRolls),
        get(Stat.BaseChanceToDealTripleDamagePct), get(Stat.MarauderHiddenAscendancyDamagePlusPctFinal),
        get(Stat.TreatEnemyResistancesAsNegatedOnElementalDamageHitPctChance),
        get(Stat.EnemyHitsPctChanceToTreatElementalResistancesAs90Pct),
        get(Stat.ChanceToDealDoubleDamagePct), get(Stat.SpellChanceToDealDoubleDamagePct));

    /// <summary>One skill's hit: the skill's own chance to deal double damage (Heavy Strike's gem) adds to the player's,
    /// a spell adds spell_chance_to_deal_double_damage_%, and the final repeat of a spell adds Awakened Spell Echo's
    /// spell_echo_plus_chance_double_damage_%_final. Attribute-scaled skill stats use complete groups of ten.</summary>
    public HitRollModifiers ForSkill(Func<uint, int> skill, bool spell, bool finalRepeat = false, int intelligence = 0) => this with
    {
        DoubleDamageChance = ActorScale.Saturate((long)DoubleDamageChance + skill(Stat.ChanceToDealDoubleDamagePct)
            + (long)(Math.Max(0, intelligence) / 10) * skill(Stat.ChanceToDealDoubleDamagePctPer10Intelligence)
            + (spell ? SpellDoubleDamageChance : 0)
            + (spell && finalRepeat ? skill(Stat.SpellEchoPlusChanceDoubleDamagePctFinal) : 0)),
    };

    /// <summary>"Attacks with this Weapon deal Double Damage" (Paradoxica's weapon-local local_double_damage_with_attacks):
    /// every hit of an attack made with that weapon doubles. <paramref name="weaponLocal"/> holds the local stats of the
    /// hand that made the hit (null for a spell or a shield's swing), so the other hand's weapon never doubles it.</summary>
    public HitRollModifiers ForWeapon(IReadOnlyDictionary<uint, int>? weaponLocal) =>
        weaponLocal?.GetValueOrDefault(Stat.LocalDoubleDamageWithAttacks) is > 0 ? this with { DoubleDamageChance = 100 } : this;

    /// <summary>Growing Accumulator's less damage applies to hits and damage over time; triple damage only to hits.</summary>
    public bool InvertsResistance(Random random) => RandomRolls.Chance(InvertElementalResistanceChance, 100, random);
    public bool InvertsResistance(Random random, int skillChance) =>
        RandomRolls.Chance(ActorScale.Saturate((long)InvertElementalResistanceChance + skillChance), 100, random);
    public bool UsesNinetyResistance(Random random) => RandomRolls.Chance(EnemyHitsNinetyResistanceChance, 100, random);

    public double DamageFactor => Math.Max(0, (100.0 + DamageMorePct) / 100.0);

    internal HitRollModifiers ForCriticalStrike(bool critical, IReadOnlyDictionary<uint, int> caster,
        IReadOnlyDictionary<uint, int>? skill) => critical ? WithCriticalDoubleDamage(CriticalDoubleDamageOf(caster, skill)) : this;

    internal static long CriticalDoubleDamageOf(IReadOnlyDictionary<uint, int> caster, IReadOnlyDictionary<uint, int>? skill) =>
        (long)caster.GetValueOrDefault(Stat.CriticalStrikePctChanceToDealDoubleDamage)
            + (skill?.GetValueOrDefault(Stat.CriticalStrikePctChanceToDealDoubleDamage) ?? 0);

    HitRollModifiers WithCriticalDoubleDamage(long chance) => this with
    {
        DoubleDamageChance = ActorScale.Saturate(DoubleDamageChance + chance),
    };

    // Ordinary hits keep their existing draw order. A critical-only chance defers the one multiplier roll
    // until the hit has confirmed its critical strike.
    internal DamagePacket RollBeforeCritical(DamagePacket hit, Random random, long criticalDoubleDamage) =>
        criticalDoubleDamage == 0 ? RollOutgoingHit(hit, random)
            : (DamageFactor == 1 ? hit : hit.Scale(DamageFactor)).Roll(random, DamageExtraRolls);

    internal DamagePacket CompleteCriticalMultiplier(DamagePacket rolled, Random random, bool critical, long criticalDoubleDamage) =>
        criticalDoubleDamage == 0 ? rolled
            : (critical ? WithCriticalDoubleDamage(criticalDoubleDamage) : this).ApplyDamageMultiplier(rolled, random);

    internal DamagePacket ApplyDamageMultiplier(DamagePacket rolled, Random random)
    {
        int multiplier = RollDamageMultiplier(random);
        return multiplier == 1 ? rolled : rolled.Scale(factor: multiplier);
    }

    /// <summary>Apply the less damage and roll the triple-damage chance, then the double-damage one, then roll each damage
    /// type independently. Critical strike multiplication and target mitigation still belong to the caller.</summary>
    public DamagePacket RollOutgoingHit(DamagePacket hit, Random random) =>
        hit.Scale(DamageFactor * RollDamageMultiplier(random)).Roll(random, DamageExtraRolls);

    // Triple damage overrides double, so double only rolls when triple misses.
    int RollDamageMultiplier(Random random) => RandomRolls.Chance(TripleDamageChance, 100, random) ? 3
            : RandomRolls.Chance(DoubleDamageChance, 100, random) ? 2 : 1;

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
    public DamagePacket MitigateIncomingHit(DamagePacket hit, Func<DamageType, DamageModel.ResistanceValue> resistance, Random random) =>
        MitigateIncomingHit(hit, resistance, random, out _);

    /// <summary><paramref name="ninety"/> is the hit's one roll, for a caller that reports each part's resistance.</summary>
    public DamagePacket MitigateIncomingHit(DamagePacket hit, Func<DamageType, DamageModel.ResistanceValue> resistance, Random random,
        out bool ninety)
    {
        bool chosen = RandomRolls.Chance(EnemyHitsNinetyResistanceChance, 100, random);
        ninety = chosen;
        return hit.Select((type, range) =>
        {
            if (type == DamageType.Physical) return range; // physical armour/reduction is a separate caller stage
            var r = chosen && DamageTypes.IsElemental(type) ? new DamageModel.ResistanceValue(90, 90) : resistance(type);
            return new(DamageModel.Mitigate(range.Min, r.Value, r.Maximum), DamageModel.Mitigate(range.Max, r.Value, r.Maximum));
        });
    }
}
