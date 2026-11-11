using GameServer.Core.World;


public static class SupportedAttackDamage
{
    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        return PlayerHitDamage.Resolve(Raw(weapon, get), get, new DamageConversion(), spell: false,
            melee: types.Contains(19), area: types.Contains(7), projectile: types.Contains(2), skillTypes: types,
            wieldingWand: weapon.WieldingWand, retaliation: types.Contains(120));
    }

    /// <summary>The weapon's own damage (its local packet, or its base physical plus any local added components a
    /// test or main's ItemWeapons constructs separately) with the global added ranges, before any increase. A bow's
    /// attacks also take the items' "Adds # to # ... Damage with Bows" stats (two spellings in the tables).</summary>
    public static DamagePacket Raw(SkillCatalog.Unarmed weapon, Func<string, int> get)
    {
        var packet = DamageStats.RawAdded(weapon.LocalDamage ?? DamagePacket.Of(DamageType.Physical,
            new(weapon.MinDamage, weapon.MaxDamage)).Add(weapon.LocalAdded), get, spell: false);
        if (!weapon.Bow) return packet;
        foreach (var type in new[] { DamageType.Physical, DamageType.Lightning, DamageType.Cold, DamageType.Fire, DamageType.Chaos })
        {
            string slug = type.ToString().ToLowerInvariant();
            var bow = new DamageRange(
                get($"attack_minimum_added_{slug}_damage_with_bow") + get($"attack_minimum_added_{slug}_damage_with_bows"),
                get($"attack_maximum_added_{slug}_damage_with_bow") + get($"attack_maximum_added_{slug}_damage_with_bows"));
            if (bow.Min != 0 || bow.Max != 0) packet = packet.Add(type, bow);
        }
        return packet;
    }
}
