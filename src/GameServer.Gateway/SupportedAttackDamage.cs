using GameServer.Core.World;

static class SupportedAttackDamage
{
    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        var hit = DamagePacket.Of(DamageType.Physical, new(weapon.MinDamage, weapon.MaxDamage));
        double manaPhysical = DamageStats.ManaAddedPhysical(get);
        hit = hit.Add(DamageType.Physical, new(manaPhysical, manaPhysical));
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            hit = hit.Add(type, new(get($"attack_minimum_added_{slug}_damage"), get($"attack_maximum_added_{slug}_damage")));
        }
        double manaLightning = DamageStats.ManaAddedLightning(get);
        hit = hit.Add(DamageType.Lightning, new(manaLightning, manaLightning));
        return hit.Select((type, range) =>
        {
            string slug = type.ToString().ToLowerInvariant();
            double increased = (double)get("damage_+%") + get("attack_damage_+%") + get($"{slug}_damage_+%")
                + (DamageTypes.IsElemental(type) ? get("elemental_damage_+%") : 0)
                + (types.Contains(19) ? get("melee_damage_+%") : 0)
                + (types.Contains(19) && type == DamageType.Physical ? get("melee_physical_damage_+%") : 0)
                + (types.Contains(7) ? get("area_damage_+%") : 0)
                + (types.Contains(2) ? get("projectile_damage_+%") : 0)
                + DamageStats.SpellAttackIncrease(get);
            return DamageStats.IncreaseAttackRange(range, increased);
        });
    }
}
