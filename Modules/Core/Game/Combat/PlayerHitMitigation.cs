using GameServer.Core.World;


/// <summary>Incoming player hits only, after resistance and per-type rounding, before Fortification and Energy Shield.
/// Callers retain immunity, suppression and block ordering. DoT uses resistance directly and never enters this pass.</summary>
static class PlayerHitMitigation
{
    public const string ArmourAppliesToChaos = "base_armour_applies_to_chaos_damage";

    /// <summary>Reuse the ordinary armour model for chaos hits when enabled. Its denominator reads post-resistance
    /// damage (PoB CalcDefence 16de4b82, applyDmgTakenConversion); additional physical reduction stays physical.
    /// A per-hit armour decision overrides only this calculation and never writes the permanent pools.</summary>
    public static int AfterResistance(CharacterStats.Pools pools, DamageType type, int damage,
        int physicalReduction = 0, int elementalReduction = 0, int? effectiveArmour = null) => type switch
    {
        DamageType.Physical => DamageModel.MitigatePhysical(damage, effectiveArmour ?? pools.Armour, physicalReduction),
        DamageType.Chaos when pools.ArmourAppliesToChaosDamage =>
            DamageModel.Reduce(damage, DamageModel.ArmourReduction(effectiveArmour ?? pools.Armour, damage)),
        _ when DamageTypes.IsElemental(type) => DamageModel.Reduce(damage, elementalReduction),
        _ => damage,
    };
}
