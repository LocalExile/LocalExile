using GameServer.Core.World;


/// <summary>Incoming player hits only, after resistance and per-type rounding, before Fortification and Energy Shield.
/// Callers retain immunity, suppression and block ordering. DoT uses resistance directly and never enters this pass.</summary>
public static class PlayerHitMitigation
{
    public const uint ArmourAppliesToChaos = Stat.BaseArmourAppliesToChaosDamage;

    public const uint ArmourPercentToElemental = Stat.ArmourPctAppliesToFireColdLightningDamage,
        ArmourPercentToChaos = Stat.BaseArmourPctAppliesToChaosDamage;

    /// <summary>The share of armour that applies to a hit's damage of this type, 0-100 (PoB CalcDefence
    /// percentOfArmourApplies, its BASE sum capped at 100): all of it to physical, and to chaos with
    /// base_armour_applies_to_chaos_damage; else "X% of Armour applies to Fire, Cold and Lightning / Chaos Damage from
    /// Hits" (Kahuturoa's Leadership).</summary>
    public static int ArmourPercent(CharacterStats.Pools pools, DamageType type, Func<uint, int> get) => type switch
    {
        DamageType.Physical => get(Stat.KeystonePrismaticBulwark) == 0 ? 100 : 0,
        DamageType.Chaos => pools.ArmourAppliesToChaosDamage ? 100 : Math.Clamp(get(ArmourPercentToChaos), 0, 100),
        _ when DamageTypes.IsElemental(type) => get(Stat.KeystonePrismaticBulwark) != 0
            ? 100 : Math.Clamp(get(ArmourPercentToElemental), 0, 100),
        _ => 0,
    };

    /// <summary>Reuse the ordinary armour model for non-physical hits that armour applies to (<paramref name="armourPercent"/>,
    /// <see cref="ArmourPercent"/>; without it, chaos with the 100% flag). Its denominator reads post-resistance damage (PoB
    /// CalcDefence 16de4b82, applyDmgTakenConversion) and its rating is that share of armour; it adds to the elemental
    /// damage reduction under the shared cap; additional physical reduction stays physical. A per-hit armour decision
    /// overrides only this calculation and never writes the permanent pools.</summary>
    public static int AfterResistance(CharacterStats.Pools pools, DamageType type, int damage,
        int physicalReduction = 0, int elementalReduction = 0, int? effectiveArmour = null, int? armourPercent = null,
        bool imbalancedGuard = false)
    {
        int armour = effectiveArmour ?? pools.Armour;
        int cap = imbalancedGuard ? 50 : DamageModel.DamageReductionCap;
        if (imbalancedGuard) armour = ActorScale.Saturate(2L * armour);
        int share = armourPercent ?? (type == DamageType.Physical || type == DamageType.Chaos && pools.ArmourAppliesToChaosDamage ? 100 : 0);
        int fromArmour = share > 0 ? DamageModel.ArmourReduction((int)((long)armour * share / 100), damage) : 0;
        if (type == DamageType.Physical)
            return DamageModel.Reduce(damage, (int)Math.Min(cap, (long)fromArmour + Math.Max(0, physicalReduction)));
        if (DamageTypes.IsElemental(type))
            return DamageModel.Reduce(damage, (int)Math.Min(cap, (long)fromArmour + elementalReduction));
        return share > 0 ? DamageModel.Reduce(damage, Math.Min(cap, fromArmour)) : damage;
    }
}
