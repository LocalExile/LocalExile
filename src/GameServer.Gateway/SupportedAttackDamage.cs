using GameServer.Core.World;

static class SupportedAttackDamage
{
    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        var hit = DamagePacket.Of(DamageType.Physical, new(weapon.MinDamage, weapon.MaxDamage));
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            hit = hit.Add(type, new(get($"attack_minimum_added_{slug}_damage"), get($"attack_maximum_added_{slug}_damage")));
        }
        return hit.Select((type, range) =>
        {
            string slug = type.ToString().ToLowerInvariant();
            int increased = get("damage_+%") + get("attack_damage_+%") + get($"{slug}_damage_+%")
                + (DamageTypes.IsElemental(type) ? get("elemental_damage_+%") : 0)
                + (types.Contains(19) ? get("melee_damage_+%") : 0)
                + (types.Contains(19) && type == DamageType.Physical ? get("melee_physical_damage_+%") : 0)
                + (types.Contains(7) ? get("area_damage_+%") : 0)
                + (types.Contains(2) ? get("projectile_damage_+%") : 0);
            return range.Scale(Math.Max(0, 1 + increased / 100.0));
        });
    }
}
