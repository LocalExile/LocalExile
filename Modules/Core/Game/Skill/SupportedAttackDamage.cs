using GameServer.Core.World;


static class SupportedAttackDamage
{
    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        return PlayerHitDamage.Resolve(Raw(weapon, get), get, new DamageConversion(), spell: false,
            melee: types.Contains(19), area: types.Contains(7), projectile: types.Contains(2), skillTypes: types,
            wieldingWand: weapon.WieldingWand, retaliation: types.Contains(120));
    }

    /// <summary>The weapon's own damage (its local packet, or its base physical plus any local added components a
    /// test or main's ItemWeapons constructs separately) with the global added ranges, before any increase.</summary>
    public static DamagePacket Raw(SkillCatalog.Unarmed weapon, Func<string, int> get) =>
        DamageStats.RawAdded(weapon.LocalDamage ?? DamagePacket.Of(DamageType.Physical,
            new(weapon.MinDamage, weapon.MaxDamage)).Add(weapon.LocalAdded), get, spell: false);
}
