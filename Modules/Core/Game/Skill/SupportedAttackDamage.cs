using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;


public static class SupportedAttackDamage
{
    const string ColdPerDexterityMin = "attacks_with_this_weapon_minimum_added_cold_damage_per_10_dexterity",
        ColdPerDexterityMax = "attacks_with_this_weapon_maximum_added_cold_damage_per_10_dexterity";

    static readonly (DamageType Type, string Suffix)[] WandAddedRanges =
    [
        (DamageType.Physical, "physical_damage_with_wands"),
        (DamageType.Lightning, "lightning_damage_with_wand"),
        (DamageType.Cold, "cold_damage_with_wand"),
        (DamageType.Fire, "fire_damage_with_wand"),
    ];

    // This pair is IsWeaponLocal, not IsLocal: it belongs to attacks by its source weapon, not the item's base range.
    public static IReadOnlyDictionary<string, int> WeaponSources(Item item, ModCatalog mods)
    {
        var sources = ItemModStats.Local(item, mods);
        var other = Game.PlainItemSum(item, mods);
        foreach (string name in new[] { ColdPerDexterityMin, ColdPerDexterityMax })
            if (other.TryGetValue(name, out int value)) sources[name] = value;
        return sources.ToFrozenDictionary();
    }

    // The tree's "with <weapon type>" rows for the attacking hand (issue #1102, batch 1), including ModParser's
    // "with Claws" (Claw + Hit, not an ailment increase). WeaponTypeDamage reads the hand's family through
    // WeaponIdentity, so a weapon that counts as all one-hand melee types (Varunastra) takes every family's row.
    public static int WeaponIncrease(SkillCatalog.Unarmed weapon, Func<string, int> get) =>
        WeaponTypeDamage.Any(weapon, get);

    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<string, int> get, IReadOnlySet<int> types,
        AttackAccuracyProfile accuracy)
    {
        get = WeaponAttributeDamage.Read(WeaponAttributeDamage.Armed(weapon), get);
        return PlayerHitDamage.Resolve(Raw(weapon, get, accuracy), HitModifiers(weapon, get), new DamageConversion(), spell: false,
            melee: types.Contains(19), area: types.Contains(7), projectile: types.Contains(2), skillTypes: types,
            wieldingBow: weapon.Bow, wieldingWand: weapon.WieldingWand, retaliation: types.Contains(120),
            additionalIncreased: WeaponIncrease(weapon, get), additional: WeaponTypeDamage.Of(weapon, get));
    }

    // Fan of Blades' damage is "with" the attacking weapon, unlike its either-hand "while wielding" count.
    // Keep this in the existing projectile-hit increase bucket, so it cannot increase melee hits or ailments.
    public static Func<string, int> HitModifiers(SkillCatalog.Unarmed weapon, Func<string, int> get)
    {
        if (!WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, "Claw")
            && !WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, "Dagger") && weapon.ItemClass != "Rune Dagger") return get;
        int increased = get("projectile_attack_damage_+%_with_claw_or_dagger");
        return increased == 0 ? get : name => get(name) + (name == "projectile_damage_+%" ? increased : 0);
    }

    /// <summary>The weapon's own damage (its local packet, or its base physical plus any local added components a
    /// test or main's ItemWeapons constructs separately) with the global added ranges, before any increase. A bow's
    /// attacks also take the items' "Adds # to # ... Damage with Bows" stats (two spellings in the tables);
    /// wands, claws and both dagger classes take their corresponding weapon-family ranges.
    /// Item-local flat additions belong to the base coefficient; weapon-scoped attribute grants take added effectiveness.</summary>
    public static DamagePacket Raw(SkillCatalog.Unarmed weapon, Func<string, int> get, AttackAccuracyProfile accuracy,
        int baseMultiplier = 10_000, int addedEffectiveness = 10_000, double addedPhysical = 0)
    {
        var local = weapon.LocalDamage ?? DamagePacket.Of(DamageType.Physical,
            new(weapon.MinDamage, weapon.MaxDamage)).Add(weapon.LocalAdded);
        var added = DamageStats.RawAdded(DamagePacket.Empty, get, spell: false);
        if (addedPhysical != 0) added = added.Add(DamageType.Physical, new(addedPhysical, addedPhysical));
        // Use the hand/skill's resolved rating, before target-conditional hit-chance modifiers.
        long accuracyGroups = Math.Max(0, accuracy.Rating) / 200;
        added = added.Add(DamageType.Lightning, new(
            accuracyGroups * (double)get("attack_minimum_added_lightning_damage_per_200_accuracy_rating"),
            accuracyGroups * (double)get("attack_maximum_added_lightning_damage_per_200_accuracy_rating")));
        double lightRadiusIncrease = Math.Max(0, get("light_radius_+%"));
        added = added.Add(DamageType.Fire, new(
            lightRadiusIncrease * get("minimum_added_fire_damage_to_attacks_per_1%_light_radius"),
            lightRadiusIncrease * get("maximum_added_fire_damage_to_attacks_per_1%_light_radius")));
        long dexterityGroups = Math.Max(0, get("dexterity")) / 10;
        if (weapon.LocalSources is { } source && dexterityGroups > 0)
            added = added.Add(DamageType.Cold, new(
                dexterityGroups * (double)source.GetValueOrDefault(ColdPerDexterityMin),
                dexterityGroups * (double)source.GetValueOrDefault(ColdPerDexterityMax)));
        foreach (var (itemClass, family) in new[] { ("Claw", "claws"), ("Dagger", "daggers") })
            if (WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, itemClass)
                || itemClass == "Dagger" && weapon.ItemClass == "Rune Dagger")
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    string slug = type.ToString().ToLowerInvariant();
                    added = added.Add(type, new(get($"attack_minimum_added_{slug}_damage_with_{family}"),
                        get($"attack_maximum_added_{slug}_damage_with_{family}")));
                }
        if (weapon.WieldingWand)
            foreach (var (type, suffix) in WandAddedRanges)
                added = added.Add(type, new(get($"attack_minimum_added_{suffix}"), get($"attack_maximum_added_{suffix}")));
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
