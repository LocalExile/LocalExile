using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;


public static class SupportedAttackDamage
{
    const string ColdPerDexterityMin = "attacks_with_this_weapon_minimum_added_cold_damage_per_10_dexterity",
        ColdPerDexterityMax = "attacks_with_this_weapon_maximum_added_cold_damage_per_10_dexterity";

    // This pair is IsWeaponLocal, not IsLocal: it belongs to attacks by its source weapon, not the item's base range.
    public static IReadOnlyDictionary<string, int> WeaponSources(Item item, ModCatalog mods)
    {
        var sources = ItemModStats.Local(item, mods);
        var other = ItemModStats.Sum([item], mods);
        foreach (string name in new[] { ColdPerDexterityMin, ColdPerDexterityMax })
            if (other.TryGetValue(name, out int value)) sources[name] = value;
        return sources.ToFrozenDictionary();
    }

    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types)
    {
        return PlayerHitDamage.Resolve(Raw(weapon, get), get, new DamageConversion(), spell: false,
            melee: types.Contains(19), area: types.Contains(7), projectile: types.Contains(2), skillTypes: types,
            wieldingBow: weapon.Bow, wieldingWand: weapon.WieldingWand, retaliation: types.Contains(120));
    }

    /// <summary>The weapon's own damage (its local packet, or its base physical plus any local added components a
    /// test or main's ItemWeapons constructs separately) with the global added ranges, before any increase. A bow's
    /// attacks also take the items' "Adds # to # ... Damage with Bows" stats (two spellings in the tables).
    /// Item-local flat additions belong to the base coefficient; weapon-scoped attribute grants take added effectiveness.</summary>
    public static DamagePacket Raw(SkillCatalog.Unarmed weapon, Func<string, int> get,
        int baseMultiplier = 10_000, int addedEffectiveness = 10_000)
    {
        var local = weapon.LocalDamage ?? DamagePacket.Of(DamageType.Physical,
            new(weapon.MinDamage, weapon.MaxDamage)).Add(weapon.LocalAdded);
        var added = DamageStats.RawAdded(DamagePacket.Empty, get, spell: false);
        long dexterityGroups = Math.Max(0, get("dexterity")) / 10;
        if (weapon.LocalSources is { } source && dexterityGroups > 0)
            added = added.Add(DamageType.Cold, new(
                dexterityGroups * (double)source.GetValueOrDefault(ColdPerDexterityMin),
                dexterityGroups * (double)source.GetValueOrDefault(ColdPerDexterityMax)));
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
