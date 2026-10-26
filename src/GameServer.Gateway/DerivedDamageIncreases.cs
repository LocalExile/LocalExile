/// <summary>Derived increased/reduced damage, kept out of the integer source stats so a 150% transfer retains its
/// half-percent. These are additions to the ordinary increased bucket, before conversion and final multipliers.</summary>
readonly record struct DerivedDamageIncreases(double GenericPercent, double AttackOnlyPercent)
{
    public const string SpellToAttack = "additive_spell_damage_modifiers_apply_to_attack_damage_at_150%_value";
    public const string MinionToOwner = "additive_minion_damage_modifiers_apply_to_you_at_150%_value";
    public const string MinionPerDexterity = "minion_damage_+%_per_5_dex";
    public const string AttackPerArmour = "attack_damage_+%_per_450_physical_damage_reduction_rating";
    public const string AttackPerEvasion = "attack_damage_+%_per_450_evasion";

    public double AttackPercent => GenericPercent + AttackOnlyPercent;

    public static DerivedDamageIncreases From(Func<string, int> get)
    {
        // PoB 16de4b82 CalcOffence 613–622 / 651–658 copies Damage INC modifiers only. The two 150% stats are
        // flags, not percentage values: more damage, speed and critical modifiers never enter either transfer.
        double minion = get("minion_damage_+%")
            + Math.Max(0, get("dexterity")) / 5 * (double)get(MinionPerDexterity);
        double generic = get(MinionToOwner) != 0 ? minion * 1.5 : 0;
        double attack = get(SpellToAttack) != 0 ? get("spell_damage_+%") * 1.5 : 0;
        // Only complete groups of the final ratings count. In particular, chance to double armour against a hit
        // does not change the character's rating, and base defence inputs must not be counted again here.
        attack += Math.Max(0, get("physical_damage_reduction_rating")) / 450 * (double)get(AttackPerArmour)
            + Math.Max(0, get("evasion_rating")) / 450 * (double)get(AttackPerEvasion);
        return new(generic, attack);
    }
}
