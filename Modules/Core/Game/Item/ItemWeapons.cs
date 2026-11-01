using GameServer.Core.World;


/// <summary>
/// A weapon's attack from its base WeaponTypes row and the item's local stats (13-items.md §2c): local physical damage
/// (flat added and increased), weapon quality (+1% local physical damage per quality, a separate multiplier after the
/// local increases since 3.25), local attack speed, and the local added elemental/chaos damage the weapon adds to every
/// hit (scaled by the local elemental damage increases; physical quality does not touch it).
/// </summary>
static class ItemWeapons
{
    public static SkillCatalog.Unarmed Of(int attackMs, int minDamage, int maxDamage, int range,
        IReadOnlyDictionary<string, int> local, int quality)
    {
        var result = ItemWeaponStats.Calculate((attackMs, minDamage, maxDamage, range),
            name => local.GetValueOrDefault(name), quality);
        var physical = result.Damage[DamageType.Physical];
        return new SkillCatalog.Unarmed(result.AttackMs, (int)physical.Min, (int)physical.Max, range)
            { LocalDamage = result.Damage };
    }
}
