/// <summary>Modern Arcane Might grants. Consumes resolved spell INC/RED; does not produce spell modifiers.</summary>
static class ArcaneMight
{
    public const string Basic = "additive_spell_damage_modifiers_apply_to_attack_damage";
    public const string Ratio = "additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value";
    public const string Fixed150 = "additive_spell_damage_modifiers_apply_to_attack_damage_at_150%_value";
    public const string Wand = "additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value_while_wielding_wand";
    public const string Retaliation = "additive_spell_damage_modifiers_apply_to_retaliation_attack_damage_at_%_value";
    public const string Skill = "active_skill_additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value";

    public static double Increase(Func<string, int> get, bool wieldingWand = false, bool retaliation = false)
    {
        int ratio = Math.Max(get(Basic) != 0 ? 100 : 0, get(Fixed150) != 0 ? 150 : 0);
        ratio = Math.Max(ratio, Math.Max(get(Ratio), get(Skill)));
        if (wieldingWand) ratio = Math.Max(ratio, get(Wand));
        if (retaliation) ratio = Math.Max(ratio, get(Retaliation));
        // Choose the ratio before multiplying: reductions must use the strongest grant too.
        return get("spell_damage_+%") * (ratio / 100.0);
    }

    public static double Increase(IReadOnlyDictionary<uint, int>? stats, bool wieldingWand = false, bool retaliation = false)
    {
        int Get(string name) => stats?.GetValueOrDefault(name switch
        {
            Basic => StatKeys.SpellDamageIncreasesApplyToAttacks,
            Ratio => StatKeys.SpellDamageIncreasesApplyToAttacksRatio,
            Fixed150 => StatKeys.SpellDamageIncreasesApplyToAttacks150,
            Wand => StatKeys.SpellDamageIncreasesApplyToAttacksWithWand,
            Retaliation => StatKeys.SpellDamageIncreasesApplyToRetaliation,
            Skill => StatKeys.SkillSpellDamageIncreasesApplyToAttacks,
            _ => StatKeys.SpellDamagePct,
        }) ?? 0;
        return Increase(Get, wieldingWand, retaliation);
    }

    public static bool IsGrant(uint key) => key is StatKeys.SpellDamageIncreasesApplyToAttacks
        or StatKeys.SpellDamageIncreasesApplyToAttacksRatio or StatKeys.SpellDamageIncreasesApplyToAttacks150
        or StatKeys.SpellDamageIncreasesApplyToAttacksWithWand or StatKeys.SpellDamageIncreasesApplyToRetaliation
        or StatKeys.SkillSpellDamageIncreasesApplyToAttacks;

    // Only independent grants use this merge. A skill's own base + quality still adds in EffectStats.
    public static int Combine(uint key, int a, int b) => key switch
    {
        StatKeys.SpellDamageIncreasesApplyToAttacks or StatKeys.SpellDamageIncreasesApplyToAttacks150
            => a != 0 || b != 0 ? 1 : 0,
        _ => IsGrant(key) ? Math.Max(a, b) : ActorScale.Add(key, a, b),
    };

    public static int Combine(string name, int a, int b) => name switch
    {
        Basic or Fixed150 => a != 0 || b != 0 ? 1 : 0,
        Ratio or Wand or Retaliation or Skill => Math.Max(a, b),
        _ => ActorScale.Add(name, a, b),
    };
}
