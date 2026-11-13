using GameServer.Core.World;


public static class SupportedAttackDamage
{
    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        return PlayerHitDamage.Resolve(Raw(weapon, get), get, new DamageConversion(), spell: false,
            melee: types.Contains(19), area: types.Contains(7), projectile: types.Contains(2), skillTypes: types,
            wieldingBow: weapon.Bow, wieldingWand: weapon.WieldingWand, retaliation: types.Contains(120));
    }

    /// <summary>The weapon's own damage (its local packet, or its base physical plus any local added components a
    /// test or main's ItemWeapons constructs separately) with the global added ranges, before any increase. A bow's
    /// attacks also take the items' "Adds # to # ... Damage with Bows" stats (two spellings in the tables).
    /// Weapon-local additions belong to the base coefficient; all other additions take added effectiveness.</summary>
    public static DamagePacket Raw(SkillCatalog.Unarmed weapon, Func<string, int> get,
        int baseMultiplier = 10_000, int addedEffectiveness = 10_000)
    {
        var local = weapon.LocalDamage ?? DamagePacket.Of(DamageType.Physical,
            new(weapon.MinDamage, weapon.MaxDamage)).Add(weapon.LocalAdded);
        var added = DamageStats.RawAdded(DamagePacket.Empty, get, spell: false);
        if (weapon.Bow)
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                var bow = new DamageRange(
                    get($"attack_minimum_added_{slug}_damage_with_bow") + get($"attack_minimum_added_{slug}_damage_with_bows"),
                    get($"attack_maximum_added_{slug}_damage_with_bow") + get($"attack_maximum_added_{slug}_damage_with_bows"));
                if (bow.Min != 0 || bow.Max != 0) added = added.Add(type, bow);
            }
        return WithCoefficients(local, added, baseMultiplier, addedEffectiveness);
    }

    public static DamagePacket WithCoefficients(DamagePacket local, DamagePacket added,
        int baseMultiplier, int addedEffectiveness) => Scale(local, baseMultiplier).Add(Scale(added, addedEffectiveness));

    // Multiply before dividing so integral endpoints survive later hit/panel flooring.
    static DamagePacket Scale(DamagePacket packet, int coefficient) => coefficient == 10_000 ? packet
        : packet.Select((_, range) => new(range.Min * Math.Max(0, coefficient) / 10_000.0,
            range.Max * Math.Max(0, coefficient) / 10_000.0));
}
