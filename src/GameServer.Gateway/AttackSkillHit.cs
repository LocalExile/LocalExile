using GameServer.Core.World;

/// <summary>PR #198's shared raw-character attack packet, adapted to typed derived damage, Rage and melee context.
/// The gem contributes effectiveness and quality-aware conversion before critical strikes and target mitigation.</summary>
static class AttackSkillHit
{
    public static DamagePacket BaseHit(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> character,
        StatCatalog catalog, bool melee, int baseMultiplier = 10000, int addedEffectiveness = 10000)
    {
        int Get(string name) => catalog.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        var derived = DerivedDamageIncreases.From(Get);
        var weaponBase = weapon.HitDamage(character, melee, derived.AttackPercent).Scale(Math.Max(0, baseMultiplier) / 10000.0);
        return DamageStats.Added(weaponBase, Get, spell: false, melee,
                effectiveness: Math.Max(0, addedEffectiveness) / 10000.0, additionalIncreasedPct: derived.AttackPercent)
            .Scale(Math.Max(0, 1 + Get("attack_damage_+%_final_from_rage") / 100.0) * ChargeEffects.DamageMore(Get));
    }

    public static DamagePacket Resolve(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> character,
        IEnumerable<(uint, int)> levelConversionInputs, StatCatalog catalog, int effectiveness, bool melee,
        IEnumerable<(uint, int)>? qualityConversionInputs = null, int? baseMultiplier = null)
    {
        var level = levelConversionInputs.ToArray();
        var quality = qualityConversionInputs?.ToArray() ?? [];
        // Skill-local damage modifiers and global character modifiers share the additive source pool. In particular,
        // PoB ModList.Sum(BASE) includes its actor parent: Archmage Power Siphon's own mana addition adds to Whisperer.
        // Use raw inputs, never the already calculated main-hand damage rows, for both combat and selected-skill rows.
        var hit = BaseHit(weapon, Sum(character, level, quality), catalog, melee, baseMultiplier ?? effectiveness, effectiveness);
        // Match quality-aware ConversionFor: raw player keys win over level inputs, then quality adds once.
        var conversionInputs = new Dictionary<uint, int>(character);
        foreach (var (key, value) in level) conversionInputs.TryAdd(key, value);
        conversionInputs = Sum(conversionInputs, quality);
        return hit.Convert(DamageStats.Conversion(conversionInputs, catalog, attack: true));
    }

    static Dictionary<uint, int> Sum(IReadOnlyDictionary<uint, int> character, params IEnumerable<(uint, int)>[] inputs)
    {
        var values = character.ToDictionary(kv => kv.Key, kv => (long)kv.Value);
        foreach (var input in inputs)
            foreach (var (key, value) in input) values[key] = values.GetValueOrDefault(key) + value;
        return values.ToDictionary(kv => kv.Key, kv => (int)Math.Clamp(kv.Value, int.MinValue, int.MaxValue));
    }
}
