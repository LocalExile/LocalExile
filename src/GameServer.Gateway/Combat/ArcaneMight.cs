/// <summary>Modern Arcane Might grants. Consumes resolved spell INC/RED; does not produce spell modifiers.</summary>
public static class ArcaneMight
{
    public const uint Basic = Stat.AdditiveSpellDamageModifiersApplyToAttackDamage;
    public const uint Ratio = Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAtPctValue;
    public const uint Fixed150 = Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAt150PctValue;
    public const uint Wand = Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAtPctValueWhileWieldingWand;
    public const uint Retaliation = Stat.AdditiveSpellDamageModifiersApplyToRetaliationAttackDamageAtPctValue;
    public const uint Skill = Stat.ActiveSkillAdditiveSpellDamageModifiersApplyToAttackDamageAtPctValue;

    public static double Increase(Func<uint, int> get, bool wieldingWand = false, bool retaliation = false)
    {
        int ratio = Math.Max(get(Basic) != 0 ? 100 : 0, get(Fixed150) != 0 ? 150 : 0);
        ratio = Math.Max(ratio, Math.Max(get(Ratio), get(Skill)));
        if (wieldingWand) ratio = Math.Max(ratio, get(Wand));
        if (retaliation) ratio = Math.Max(ratio, get(Retaliation));
        // Choose the ratio before multiplying: reductions must use the strongest grant too.
        return get(Stat.SpellDamagePlusPct) * (ratio / 100.0);
    }

    public static double Increase(IReadOnlyDictionary<uint, int>? stats, bool wieldingWand = false, bool retaliation = false)
    {
        int Get(uint name) => stats?.GetValueOrDefault(name switch
        {
            Basic => Stat.AdditiveSpellDamageModifiersApplyToAttackDamage,
            Ratio => Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAtPctValue,
            Fixed150 => Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAt150PctValue,
            Wand => Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAtPctValueWhileWieldingWand,
            Retaliation => Stat.AdditiveSpellDamageModifiersApplyToRetaliationAttackDamageAtPctValue,
            Skill => Stat.ActiveSkillAdditiveSpellDamageModifiersApplyToAttackDamageAtPctValue,
            _ => Stat.SpellDamagePlusPct,
        }) ?? 0;
        return Increase(Get, wieldingWand, retaliation);
    }

    public static bool IsGrant(uint key) => key is Stat.AdditiveSpellDamageModifiersApplyToAttackDamage
        or Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAtPctValue or Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAt150PctValue
        or Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAtPctValueWhileWieldingWand or Stat.AdditiveSpellDamageModifiersApplyToRetaliationAttackDamageAtPctValue
        or Stat.ActiveSkillAdditiveSpellDamageModifiersApplyToAttackDamageAtPctValue;

    // Only independent grants use this merge. A skill's own base + quality still adds in EffectStats.
    public static int Combine(uint key, int a, int b) => key switch
    {
        Stat.AdditiveSpellDamageModifiersApplyToAttackDamage or Stat.AdditiveSpellDamageModifiersApplyToAttackDamageAt150PctValue
            => a != 0 || b != 0 ? 1 : 0,
        _ => IsGrant(key) ? Math.Max(a, b) : ActorScale.Add(key, a, b),
    };
}
