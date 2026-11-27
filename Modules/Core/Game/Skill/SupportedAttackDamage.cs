using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;


public static class SupportedAttackDamage
{
    const uint ColdPerDexterityMin = Stat.AttacksWithThisWeaponMinimumAddedColdDamagePer10Dexterity,
        ColdPerDexterityMax = Stat.AttacksWithThisWeaponMaximumAddedColdDamagePer10Dexterity,
        EphemeralEdgeLightningFromEs = Stat.EphemeralEdgeMaximumLightningDamageFromEsPct;

    static readonly (DamageType Type, uint Min, uint Max)[] WandAddedRanges =
    [
        (DamageType.Physical, Stat.Of("attack_minimum_added_physical_damage_with_wands"), Stat.Of("attack_maximum_added_physical_damage_with_wands")),
        (DamageType.Lightning, Stat.Of("attack_minimum_added_lightning_damage_with_wand"), Stat.Of("attack_maximum_added_lightning_damage_with_wand")),
        (DamageType.Cold, Stat.Of("attack_minimum_added_cold_damage_with_wand"), Stat.Of("attack_maximum_added_cold_damage_with_wand")),
        (DamageType.Fire, Stat.Of("attack_minimum_added_fire_damage_with_wand"), Stat.Of("attack_maximum_added_fire_damage_with_wand")),
    ];

    static readonly (string ItemClass, (DamageType Type, uint Min, uint Max)[] Ranges)[] MeleeFamilyAddedRanges =
    [
        ("Claw", [.. DamageTypes.ConversionOrder.Select(t => (t, Stat.Of($"attack_minimum_added_{t.ToString().ToLowerInvariant()}_damage_with_claws"),
            Stat.Of($"attack_maximum_added_{t.ToString().ToLowerInvariant()}_damage_with_claws")))]),
        ("Dagger", [.. DamageTypes.ConversionOrder.Select(t => (t, Stat.Of($"attack_minimum_added_{t.ToString().ToLowerInvariant()}_damage_with_daggers"),
            Stat.Of($"attack_maximum_added_{t.ToString().ToLowerInvariant()}_damage_with_daggers")))]),
    ];

    static readonly (DamageType Type, uint Min1, uint Min2, uint Max1, uint Max2)[] BowAddedRanges =
        [.. DamageTypes.ConversionOrder.Select(t => (t,
            Stat.Of($"attack_minimum_added_{t.ToString().ToLowerInvariant()}_damage_with_bow"),
            Stat.Of($"attack_minimum_added_{t.ToString().ToLowerInvariant()}_damage_with_bows"),
            Stat.Of($"attack_maximum_added_{t.ToString().ToLowerInvariant()}_damage_with_bow"),
            Stat.Of($"attack_maximum_added_{t.ToString().ToLowerInvariant()}_damage_with_bows")))];

    // These weapon-scoped stats belong to attacks by their source hand; base weapon construction reads the local packet.
    public static IReadOnlyDictionary<uint, int> WeaponSources(Item item, ModCatalog mods)
    {
        var sources = ItemModStats.Local(item, mods);
        var other = Game.PlainItemSum(item, mods);
        foreach (uint name in new[] { ColdPerDexterityMin, ColdPerDexterityMax, Stat.WeaponPhysicalDamagePctToAddAsEachElement,
            Stat.HitsHaveCriticalStrikeMultiplerPlusPerMonsterPower, Stat.HitsHaveLeechPctIsInstantPerMonsterPower,
            Stat.LocalAttacksWithThisWeaponElementalDamagePlusPct })
            if (other.TryGetValue(name, out int value)) sources[name] = value;
        return sources.ToFrozenDictionary();
    }

    // The tree's "with <weapon type>" rows for the attacking hand (issue #1102, batch 1), including ModParser's
    // "with Claws" (Claw + Hit, not an ailment increase). WeaponTypeDamage reads the hand's family through
    // WeaponIdentity, so a weapon that counts as all one-hand melee types (Varunastra) takes every family's row.
    public static int WeaponIncrease(SkillCatalog.Unarmed weapon, Func<uint, int> get) =>
        WeaponIncrease(weapon, get, skillProjectileSpeed: 0);

    internal static int WeaponIncrease(SkillCatalog.Unarmed weapon, Func<uint, int> get, int skillProjectileSpeed)
    {
        int increased = WeaponTypeDamage.Any(weapon, get);
        if (weapon.Bow && get(Stat.ArrowSpeedAdditiveModifiersAlsoApplyToBowDamage) != 0)
            increased = ActorScale.Saturate((long)increased + get(Stat.ProjectileSpeedPlusPct) + skillProjectileSpeed);
        return increased;
    }

    public static void AddWandGains(DamageConversion conversion, Func<uint, int> get)
    {
        conversion.AddGain(DamageType.Physical, DamageType.Fire, Math.Max(0, get(Stat.WandPhysicalDamagePctToAddAsFire)));
        conversion.AddGain(DamageType.Physical, DamageType.Cold, Math.Max(0, get(Stat.WandPhysicalDamagePctToAddAsCold)));
        conversion.AddGain(DamageType.Physical, DamageType.Lightning, Math.Max(0, get(Stat.WandPhysicalDamagePctToAddAsLightning)));
        conversion.AddGain(DamageType.Physical, DamageType.Chaos, Math.Max(0, get(Stat.WandPhysicalDamagePctToAddAsChaos)));
    }

    public static DamagePacket Hit(SkillCatalog.Unarmed weapon, Func<uint, int> get, IReadOnlySet<int> types,
        AttackAccuracyProfile accuracy)
    {
        get = WeaponAttributeDamage.Read(WeaponAttributeDamage.Armed(weapon), get);
        return PlayerHitDamage.Resolve(Raw(weapon, get, accuracy, melee: types.Contains(19)), HitModifiers(weapon, get, accuracy.Rating),
            WithWeaponGains(new(), weapon, get), spell: false,
            melee: types.Contains(19), area: types.Contains(7), projectile: types.Contains(2), skillTypes: types,
            wieldingBow: weapon.Bow, wieldingWand: weapon.WieldingWand, retaliation: types.Contains(120),
            additionalIncreased: WeaponIncrease(weapon, get), additional: WeaponTypeDamage.Of(weapon, get));
    }

    // Fan of Blades' damage is "with" the attacking weapon, unlike its either-hand "while wielding" count.
    // Keep this in the existing projectile-hit increase bucket, so it cannot increase melee hits or ailments.
    // "Increased Projectile Attack Damage" is the same bucket for every attack.
    public static Func<uint, int> HitModifiers(SkillCatalog.Unarmed weapon, Func<uint, int> get) => HitModifiers(weapon, get, 0);

    public static Func<uint, int> HitModifiers(SkillCatalog.Unarmed weapon, Func<uint, int> get, int accuracy)
    {
        long increased = (long)get(Stat.ProjectileAttackDamagePlusPct)
            + (long)get(Stat.ProjectileAttackDamagePlusPctPer200Accuracy) * (Math.Max(0, accuracy) / 200);
        if (WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, "Claw")
            || WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, "Dagger") || weapon.ItemClass == "Rune Dagger")
            increased += get(Stat.ProjectileAttackDamagePlusPctWithClawOrDagger);
        return increased == 0 ? get : name => name == Stat.ProjectileDamagePlusPct
            ? ActorScale.Saturate(get(name) + increased) : get(name);
    }

    /// <summary>The weapon's own damage (its local packet, or its base physical plus any local added components a
    /// test or main's ItemWeapons constructs separately) with the global added ranges, before any increase. A bow's
    /// attacks also take the items' "Adds # to # ... Damage with Bows" stats (two spellings in the tables);
    /// wands, claws and both dagger classes take their corresponding weapon-family ranges.
    /// Item-local flat additions belong to the base coefficient; weapon-scoped attribute grants take added effectiveness.</summary>
    public static DamagePacket Raw(SkillCatalog.Unarmed weapon, Func<uint, int> get, AttackAccuracyProfile accuracy,
        int baseMultiplier = 10_000, int addedEffectiveness = 10_000, bool melee = false)
    {
        var local = WeaponBase(weapon);
        var added = DamageStats.RawAdded(DamagePacket.Empty, get, spell: false);
        if (melee && !WeaponAttributeDamage.Armed(weapon))
            added = added.Add(DamageType.Lightning, new DamageRange(
                get(Stat.AttackMinimumAddedMeleeLightningDamageWhileUnarmed),
                get(Stat.AttackMaximumAddedMeleeLightningDamageWhileUnarmed)));
        // Use the hand/skill's resolved rating, before target-conditional hit-chance modifiers.
        long accuracyGroups = Math.Max(0, accuracy.Rating) / 200;
        added = added.Add(DamageType.Lightning, new(
            accuracyGroups * (double)get(Stat.AttackMinimumAddedLightningDamagePer200AccuracyRating),
            accuracyGroups * (double)get(Stat.AttackMaximumAddedLightningDamagePer200AccuracyRating)));
        double lightRadiusIncrease = Math.Max(0, get(Stat.LightRadiusPlusPct));
        added = added.Add(DamageType.Fire, new(
            lightRadiusIncrease * get(Stat.MinimumAddedFireDamageToAttacksPer1PctLightRadius),
            lightRadiusIncrease * get(Stat.MaximumAddedFireDamageToAttacksPer1PctLightRadius)));
        long dexterityGroups = Math.Max(0, get(Stat.Dexterity)) / 10;
        if (weapon.LocalSources is { } source)
        {
            if (dexterityGroups > 0)
                added = added.Add(DamageType.Cold, new(
                    dexterityGroups * (double)source.GetValueOrDefault(ColdPerDexterityMin),
                    dexterityGroups * (double)source.GetValueOrDefault(ColdPerDexterityMax)));
            int esPct = source.GetValueOrDefault(EphemeralEdgeLightningFromEs);
            if (esPct > 0 && get(Stat.MaximumEnergyShield) is int maxEnergyShield and > 0)
                added = added.Add(DamageType.Lightning, new(0, (long)maxEnergyShield * esPct / 100.0));
            int lifePct = source.GetValueOrDefault(Stat.LocalFireDamageFromLifePct);
            if (lifePct > 0 && get(Stat.MaximumLife) is int life and > 0)
            {
                double fire = (long)life * lifePct / 100.0;
                added = added.Add(DamageType.Fire, new(fire, fire));
            }
        }
        foreach (var (itemClass, ranges) in MeleeFamilyAddedRanges)
            if (WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, itemClass)
                || itemClass == "Dagger" && weapon.ItemClass == "Rune Dagger")
                foreach (var (type, min, max) in ranges)
                    added = added.Add(type, new(get(min), get(max)));
        if (weapon.WieldingWand)
            foreach (var (type, min, max) in WandAddedRanges)
                added = added.Add(type, new(get(min), get(max)));
        if (weapon.Bow)
            foreach (var (type, min1, min2, max1, max2) in BowAddedRanges)
            {
                var bow = new DamageRange(get(min1) + get(min2), get(max1) + get(max2));
                if (bow.Min != 0 || bow.Max != 0) added = added.Add(type, bow);
            }
        return WithCoefficients(local, added, baseMultiplier, addedEffectiveness);
    }

    static DamagePacket WeaponBase(SkillCatalog.Unarmed weapon) => weapon.LocalDamage
        ?? DamagePacket.Of(DamageType.Physical, new(weapon.MinDamage, weapon.MaxDamage)).Add(weapon.LocalAdded);

    internal static DamageConversion WithWeaponGains(DamageConversion conversion, SkillCatalog.Unarmed weapon,
        Func<uint, int>? get = null)
    {
        if (!WeaponAttributeDamage.Armed(weapon)) return conversion;
        int gain = weapon.LocalSources?.GetValueOrDefault(Stat.WeaponPhysicalDamagePctToAddAsEachElement) ?? 0;
        if (gain > 0)
            conversion.AddGain(DamageType.Physical, DamageType.Lightning, gain)
                .AddGain(DamageType.Physical, DamageType.Cold, gain)
                .AddGain(DamageType.Physical, DamageType.Fire, gain);
        if (weapon.WieldingWand && get is not null)
        {
            conversion.AddGlobal(DamageType.Physical, DamageType.Lightning, get(Stat.WandPhysicalDamagePctToConvertToLightning));
            AddWandGains(conversion, get);
        }
        return conversion;
    }

    public static DamagePacket WithCoefficients(DamagePacket local, DamagePacket added,
        int baseMultiplier, int addedEffectiveness) => Scale(local, baseMultiplier).Add(Scale(added, addedEffectiveness));

    // Multiply before dividing so integral endpoints survive later hit/panel flooring.
    static DamagePacket Scale(DamagePacket packet, int coefficient) => coefficient == 10_000 ? packet
        : packet.Select((_, range) => new(range.Min * Math.Max(0, coefficient) / 10_000.0,
            range.Max * Math.Max(0, coefficient) / 10_000.0));
}
