using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// The pilot radius unique jewels: the six attribute-conversion jewels and the five damage/defence-conversion jewels.
/// Each item carries the client's local <c>local_unique_jewel_*</c> conversion mod (and, where the real item has one, a
/// global attribute/defence mod). The client draws the converted passives from the local mod; the server applies the
/// same stat move to the allocated passives in the item's radius. Lioneye's Fall is not in this catalogue yet: its
/// melee-to-bow change needs the weapon/melee modifier flags the server's stat model does not carry.
/// </summary>
public static class RadiusJewelCatalogue
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        MeleeToBow = LoadMeleeToBow();
        NameWords = LoadNameWords("data");
    }

    // Item mods kept pre-3.16 radii; PSG coordinates and effect circles were enlarged by 20%.
    public static int EffectRadius(int nativeRadius) => checked(nativeRadius * 6 / 5);
    public sealed record Variant(string Name, string ModId, int? Value = null);
    public sealed record Entry(string Name, string Base, string[] GlobalMods, string ConversionMod,
        int Limit = 0, byte Level = 1, bool Corrupted = false, int Radius = 0, Variant[]? Variants = null,
        string[]? ExtraConversionMods = null, bool VariantKeepsConversionMod = false);

    public static Entry[] All => GameDataCatalog.RadiusJewels;

    public static string[] Names => [.. All.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static Entry? Find(string query) =>
        All.FirstOrDefault(entry => entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(entry => $"{entry.Name} {entry.Base}".Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(entry => $"{entry.Name} {entry.Base.Replace(" Jewel", "")}"
            .Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    public static Entry? EntryOf(Item item, ModCatalog mods) =>
        All.FirstOrDefault(entry => item.Mods.Any(mod =>
        {
            string? id = mods.ByHash(mod.Hash)?.Id;
            return id == entry.ConversionMod
                || entry.ExtraConversionMods?.Contains(id) == true
                || entry.Variants?.Any(variant => variant.ModId == id) == true;
        }));

    public static bool IsSameEntry(Item item, Item other, ModCatalog mods) =>
        EntryOf(item, mods) is { } a && EntryOf(other, mods) is { } b && a.Name == b.Name;

    /// <summary>An entry and, for the Light of Meaning/Perandus Pact, the selected variant text.</summary>
    public static (Entry Entry, string? Variant)? FindWithVariant(string text)
    {
        if (Find(text) is { } exact) return (exact, null);
        foreach (var entry in All.Where(entry => entry.Variants is not null))
        {
            string prefix = entry.Name + " ";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (entry, text[prefix.Length..].Trim());
        }
        return null;
    }

    /// <summary>One source -> target stat move and its multiplier. The radius is the item's
    /// <c>local_jewel_effect_base_radius</c>.</summary>
    public sealed record Move(uint From, uint To, int Factor = 1);
    public sealed record Conversion(IReadOnlyList<Move> Moves, (uint From, uint To)? Swap, int Radius,
        IReadOnlyList<(uint Key, int Value)>? NotableStats = null,
        IReadOnlyList<(uint Key, int Value)>? AdditionStats = null,
        ResistanceAddition? Resistance = null,
        IReadOnlyList<ScalingStat>? Scaling = null,
        int EffectPct = 0,
        bool SuppressNotables = false,
        bool AllocateDisconnected = false,
        int Ring = 0,
        bool SuppressAllocatedSmalls = false,
        bool CopyUnallocatedSmalls = false,
        bool StrengthMeleeBonus = false, int TattooEffectPct = 0, bool KeystonesOnly = false)
    {
        public uint? From => Moves.Count > 0 ? Moves[0].From : Swap?.From;
        public uint? To => Moves.Count > 0 ? Moves[0].To : Swap?.To;
    }

    /// <summary>The Thread of Hope ring selected by <c>local_jewel_variable_ring_radius_value</c>, using Path of
    /// Building 3_16's <c>jewelRadii</c> Variable entries: 1 Small, 2 Medium, 3 Large, 4 Very Large, 5 Massive.
    /// Both bounds are inclusive, matching Path of Building's
    /// <c>innerSquared &lt;= distanceSquared &lt;= outerSquared</c>.</summary>
    public static (int Inner, int Outer) RingRadii(int ring) => ring switch
    {
        1 => (960, 1320),
        2 => (1320, 1680),
        3 => (1680, 2040),
        4 => (2040, 2400),
        5 => (2400, 2880),
        _ => (0, 0),
    };

    /// <summary>Whether the jewel's allocation rule can reach this ordinary passive. Intuitive Leap accepts every
    /// passive in its disc; Thread of Hope follows the wiki and does not enable jewel sockets in its ring.</summary>
    public static bool BypassIncludes(Conversion conversion, PassiveTree.Node node) =>
        conversion.AllocateDisconnected && !(conversion.Ring > 0 && node.IsJewelSocket)
            && (!conversion.KeystonesOnly || node.IsKeystone);

    /// <summary>A per-node stat scaling: count <see cref="Count"/> on allocated or unallocated passives in radius,
    /// then add <see cref="Target"/> for every <see cref="Per"/> (or every <see cref="Value"/> when
    /// <see cref="ValueIsDivisor"/>).</summary>
    public sealed record ScalingStat(uint Target, uint Count, int Per, int Value, bool ValueIsDivisor, bool Allocated);

    /// <summary>A resistance-scaling addition: each affected passive with one of <see cref="Sources"/> also grants
    /// <see cref="Target"/> at <see cref="ScalePercent"/>% of the resistance value (100 = equal chance).</summary>
    public sealed record ResistanceAddition(IReadOnlyList<uint> Sources, uint Target, int ScalePercent)
    {
        public int Added(IReadOnlyList<(uint Key, int Value)> stats)
        {
            int total = 0;
            foreach (var (key, value) in stats)
                if (Sources.Contains(key)) total += value;
            return total * ScalePercent / 100;
        }
    }

    static readonly IReadOnlyDictionary<string, (uint From, uint To)> AttributePairs =
        new Dictionary<string, (uint From, uint To)>
        {
            ["str_to_int"] = (Stat.BaseStrength, Stat.BaseIntelligence),
            ["int_to_dex"] = (Stat.BaseIntelligence, Stat.BaseDexterity),
            ["int_to_str"] = (Stat.BaseIntelligence, Stat.BaseStrength),
            ["dex_to_int"] = (Stat.BaseDexterity, Stat.BaseIntelligence),
            ["str_to_dex"] = (Stat.BaseStrength, Stat.BaseDexterity),
            ["dex_to_str"] = (Stat.BaseDexterity, Stat.BaseStrength),
        };

    // local stat id -> target stat, count stat, per divisor, whether the value is the divisor, allocated/unallocated.
    static readonly IReadOnlyDictionary<uint, (uint Target, uint Count, int Per, bool Divisor, bool Allocated)>
        ScalingRules = new Dictionary<uint, (uint Target, uint Count, int Per, bool Divisor, bool Allocated)>
        {
            [Stat.LocalUniqueJewelAdditionalLifePerXIntInRadius] =
                (Stat.BaseMaximumLife, Stat.BaseIntelligence, 0, true, true),
            [Stat.LocalUniqueJewelChaosDamagePlusPctPer10IntInRadius] =
                (Stat.ChaosDamagePlusPct, Stat.BaseIntelligence, 10, false, true),
            [Stat.LocalUniqueJewelEvasionRatingPlusPctPerXDexInRadius] =
                (Stat.EvasionRatingPlusPct, Stat.BaseDexterity, 0, true, true),
            [Stat.LocalUniqueJewelClawPhysicalDamagePlusPctPerXDexInRadius] =
                (Stat.PhysicalClawDamagePlusPct, Stat.BaseDexterity, 0, true, true),
            [Stat.LocalUniqueJewelUnarmedDamagePlusPctPerXDexInRadius] =
                (Stat.UnarmedMeleePhysicalDamagePlusPct, Stat.BaseDexterity, 0, true, true),
            [Stat.LocalUniqueJewelTotemLifePlusXPctPer10StrInRadius] =
                (Stat.TotemLifePlusPct, Stat.BaseStrength, 10, false, true),
            [Stat.LocalUniqueJewelOneAdditionalMaximumLightningDamagePerXDex] =
                (Stat.AttackMaximumAddedLightningDamage, Stat.BaseDexterity, 0, true, true),
            [Stat.LocalUniqueJewelXStrengthPer1StrengthAllocatedInRadius] =
                (Stat.BaseStrength, Stat.BaseStrength, 1, false, true),
            [Stat.LocalUniqueJewelXIntelligencePer1IntelligenceAllocatedInRadius] =
                (Stat.BaseIntelligence, Stat.BaseIntelligence, 1, false, true),
            [Stat.LocalUniqueJewelXDexterityPer1DexterityAllocatedInRadius] =
                (Stat.BaseDexterity, Stat.BaseDexterity, 1, false, true),
            [Stat.LocalUniqueJewelCriticalStrikeMultiplierPlusPer10StrUnallocatedInRadius] =
                (Stat.BaseCriticalStrikeMultiplierPlus, Stat.BaseStrength, 10, false, false),
            [Stat.LocalUniqueJewelLifeRecoveryRatePlusPctPer10StrAllocatedInRadius] =
                (Stat.LifeRecoveryRatePlusPct, Stat.BaseStrength, 10, false, true),
            [Stat.LocalUniqueJewelLifeRecoveryRatePlusPctPer10StrUnallocatedInRadius] =
                (Stat.LifeRecoveryRatePlusPct, Stat.BaseStrength, 10, false, false),
            [Stat.LocalUniqueJewelAdditionalPhysicalDamageReductionPctPer10StrAllocatedInRadius] =
                (Stat.AdditionalPhysicalDamageReductionPct, Stat.BaseStrength, 10, false, true),
            [Stat.LocalUniqueJewelAccuracyRatingPlusPer10IntUnallocatedInRadius] =
                (Stat.AccuracyRating, Stat.BaseIntelligence, 10, false, false),
            [Stat.LocalUniqueJewelManaRecoveryRatePlusPctPer10IntAllocatedInRadius] =
                (Stat.ManaRecoveryRatePlusPct, Stat.BaseIntelligence, 10, false, true),
            [Stat.LocalUniqueJewelManaRecoveryRatePlusPctPer10IntUnallocatedInRadius] =
                (Stat.ManaRecoveryRatePlusPct, Stat.BaseIntelligence, 10, false, false),
            [Stat.LocalUniqueJewelEnergyShieldRegenerationRatePerMinutePctPer10IntAllocatedInRadius] =
                (Stat.BaseEnergyShieldRegenerationRatePerMinutePct, Stat.BaseIntelligence, 10, false, true),
            [Stat.LocalUniqueJewelDotMultiplierPlusPer10IntUnallocatedInRadius] =
                (Stat.DotMultiplierPlus, Stat.BaseIntelligence, 10, false, false),
            [Stat.LocalUniqueJewelMaximumManaPlusPer10DexUnallocatedInRadius] =
                (Stat.BaseMaximumMana, Stat.BaseDexterity, 10, false, false),
            [Stat.LocalUniqueJewelMovementSpeedPlusPctPer10DexAllocatedInRadius] =
                (Stat.BaseMovementVelocityPlusPct, Stat.BaseDexterity, 10, false, true),
            [Stat.LocalUniqueJewelMovementSpeedPlusPctPer10DexUnallocatedInRadius] =
                (Stat.BaseMovementVelocityPlusPct, Stat.BaseDexterity, 10, false, false),
            [Stat.LocalUniqueJewelAccuracyRatingPlusPer10DexUnallocatedInRadius] =
                (Stat.AccuracyRating, Stat.BaseDexterity, 10, false, false),
        };

    public static Conversion? ReadConversion(Item item, ModCatalog mods)
    {
        var moves = new List<Move>();
        var notableStats = new List<(uint Key, int Value)>();
        var additionStats = new List<(uint Key, int Value)>();
        var scalingStats = new List<ScalingStat>();
        ResistanceAddition? resistance = null;
        int radius = 0, effectPct = 0, ring = 0, tattooEffectPct = 0;
        bool physicalToCold = false, coldToPhysical = false, suppressNotables = false;
        bool allocateDisconnected = false, suppressAllocatedSmalls = false, copyUnallocatedSmalls = false;
        bool strengthMeleeBonus = false, keystonesOnly = false;
        foreach (var mod in item.Mods)
        {
            if (mods.ByHash(mod.Hash) is not { } row) continue;
            for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
            {
                uint id = row.Stats[i].Id;
                string name = row.Stats[i].Name;
                if (id == Stat.LocalUniqueJewelTattoosInRadiusEffectPlusPct)
                {
                    tattooEffectPct += mod.Values[i];
                    continue;
                }
                if (id == Stat.LocalJewelEffectBaseRadius)
                {
                    radius = mod.Values[i];
                    continue;
                }
                if (id == Stat.LocalUniqueJewelNearbyDisconnectedKeystonePassivesCanBeAllocated)
                {
                    allocateDisconnected |= mod.Values[i] != 0;
                    keystonesOnly |= mod.Values[i] != 0;
                    continue;
                }
                if (name.StartsWith("local_jewel_nearby_passives_", StringComparison.Ordinal))
                {
                    if (AttributePairs.TryGetValue(name["local_jewel_nearby_passives_".Length..], out var pair))
                        AddMove(moves, pair.From, pair.To);
                    continue;
                }
                if (name.StartsWith("local_unique_jewel_notable_passives_in_radius_instead_grant_",
                    StringComparison.Ordinal))
                {
                    string target = name["local_unique_jewel_notable_passives_in_radius_instead_grant_".Length..];
                    if (Program.Stats.Key(target) is uint key) notableStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (name.StartsWith("local_unique_jewel_passives_in_radius_grant_", StringComparison.Ordinal))
                {
                    string target = name["local_unique_jewel_passives_in_radius_grant_".Length..];
                    if (Program.Stats.Key(target) is uint key) additionStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (name.StartsWith("local_unique_jewel_passives_in_radius_give_", StringComparison.Ordinal))
                {
                    string target = name["local_unique_jewel_passives_in_radius_give_".Length..];
                    if (Program.Stats.Key(target) is uint key) additionStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (id == Stat.LocalUniqueJewelSkillsInRadiusGrantPctUnarmedMeleeAttackSpeed)
                {
                    if (Stat.UnarmedMeleeAttackSpeedPlusPct is var key)
                        additionStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (id == Stat.LocalUniqueJewelLightningResistanceAlsoGrantsPowerChargeOnKillChance)
                {
                    resistance = MakeResistance(Stat.BaseLightningDamageResistancePct, Stat.AddPowerChargeOnKillPctChance, 100);
                    continue;
                }
                if (id == Stat.LocalUniqueJewelLightningResistanceAlsoGrantsBlockSpellsChanceScaledPct)
                {
                    resistance = MakeResistance(Stat.BaseLightningDamageResistancePct, Stat.AdditionalSpellBlockPct, mod.Values[i]);
                    continue;
                }
                if (id == Stat.LocalUniqueJewelColdResistanceAlsoGrantsFrenzyChargeOnKillChance)
                {
                    resistance = MakeResistance(Stat.BaseColdDamageResistancePct, Stat.AddFrenzyChargeOnKillPctChance, 100);
                    continue;
                }
                if (id == Stat.LocalUniqueJewelColdResistanceAlsoGrantsSpellSuppressionChanceScaledPct)
                {
                    resistance = MakeResistance(Stat.BaseColdDamageResistancePct, Stat.BaseSpellSuppressionChancePct, mod.Values[i]);
                    continue;
                }
                if (id == Stat.LocalUniqueJewelFireResistanceAlsoGrantsEnduranceChargeOnKillChance)
                {
                    resistance = MakeResistance(Stat.BaseFireDamageResistancePct, Stat.EnduranceChargeOnKillPct, 100);
                    continue;
                }
                if (id == Stat.LocalUniqueJewelFireResistanceAlsoGrantsBlockChanceScaledPct)
                {
                    resistance = MakeResistance(Stat.BaseFireDamageResistancePct, Stat.AdditionalBlockPct, mod.Values[i]);
                    continue;
                }
                if (id == Stat.LocalUniqueJewelNonKeystonePassiveInRadiusEffectPlusPct)
                {
                    effectPct += mod.Values[i];
                    continue;
                }
                if (id == Stat.LocalUniqueJewelNotablePassiveInRadiusDoesNothing)
                {
                    if (mod.Values[i] != 0) suppressNotables = true;
                    continue;
                }
                if (id == Stat.LocalUniqueJewelNearbyDisconnectedPassivesCanBeAllocated)
                {
                    if (mod.Values[i] != 0) allocateDisconnected = true;
                    continue;
                }
                if (id == Stat.LocalJewelVariableRingRadiusValue)
                {
                    ring = mod.Values[i];
                    continue;
                }
                if (id == Stat.LocalJewelAllocatedNonNotablePassivesInRadiusGrantNothing)
                {
                    if (mod.Values[i] != 0) suppressAllocatedSmalls = true;
                    continue;
                }
                if (id == Stat.LocalJewelCopyStatsFromUnallocatedNonNotablePassivesInRadius)
                {
                    if (mod.Values[i] != 0) copyUnallocatedSmalls = true;
                    continue;
                }
                if (id == Stat.LocalUniqueJewelDexAndIntApplyToStrMeleeDamageBonusInRadius)
                {
                    strengthMeleeBonus = true;
                    continue;
                }
                if (ScalingRules.TryGetValue(id, out var rule))
                {
                    if (rule.Target is var target && rule.Count is var count)
                        scalingStats.Add(new(target, count, rule.Per, mod.Values[i], rule.Divisor, rule.Allocated));
                    continue;
                }
                switch (id)
                {
                    case Stat.LocalUniqueJewelPhysicalDamageIncreasesAppliesToColdDamage:
                        physicalToCold = true;
                        break;
                    case Stat.LocalUniqueJewelColdDamageIncreasesAppliesToPhysicalDamage:
                        coldToPhysical = true;
                        break;
                    case Stat.LocalUniqueJewelDamageIncreasesAppliesToFireDamage:
                        AddMove(moves, Stat.PhysicalDamagePlusPct, Stat.FireDamagePlusPct);
                        AddMove(moves, Stat.ColdDamagePlusPct, Stat.FireDamagePlusPct);
                        AddMove(moves, Stat.LightningDamagePlusPct, Stat.FireDamagePlusPct);
                        AddMove(moves, Stat.ChaosDamagePlusPct, Stat.FireDamagePlusPct);
                        AddMove(moves, Stat.ElementalDamagePlusPct, Stat.FireDamagePlusPct);
                        break;
                    case Stat.LocalUniqueJewelLifeIncreasesAppliesToEnergyShield:
                        AddMove(moves, Stat.MaximumLifePlusPct, Stat.MaximumEnergyShieldPlusPct);
                        break;
                    case Stat.LocalUniqueJewelLifeIncreasesAppliesToManaDoubled:
                        AddMove(moves, Stat.MaximumLifePlusPct, Stat.MaximumManaPlusPct, 2);
                        break;
                    case Stat.LocalUniqueJewelEnergyShieldIncreasesAppliesToArmourDoubled:
                        AddMove(moves, Stat.MaximumEnergyShieldPlusPct, Stat.PhysicalDamageReductionRatingPlusPct, 2);
                        break;
                    case Stat.LocalUniqueJewelMeleeAppliesToBow:
                        if (mod.Values[i] != 0) moves.AddRange(MeleeToBow);
                        break;
                }
            }
        }
        if (radius == 0 && EntryOf(item, mods) is { Radius: > 0 } entry) radius = entry.Radius;
        if (radius <= 0) return null;
        (uint From, uint To)? swap = null;
        if (physicalToCold && coldToPhysical)
            swap = (Stat.PhysicalDamagePlusPct, Stat.ColdDamagePlusPct);
        else
        {
            if (physicalToCold) AddMove(moves, Stat.PhysicalDamagePlusPct, Stat.ColdDamagePlusPct);
            if (coldToPhysical) AddMove(moves, Stat.ColdDamagePlusPct, Stat.PhysicalDamagePlusPct);
        }
        if (moves.Count == 0 && swap is null && notableStats.Count == 0 && additionStats.Count == 0
            && resistance is null && scalingStats.Count == 0 && effectPct == 0 && !suppressNotables
            && !allocateDisconnected && ring == 0 && !suppressAllocatedSmalls && !copyUnallocatedSmalls
            && !strengthMeleeBonus && tattooEffectPct == 0)
            return null;
        return new(moves, swap, EffectRadius(radius),
            notableStats.Count > 0 ? notableStats : null,
            additionStats.Count > 0 ? additionStats : null,
            resistance,
            scalingStats.Count > 0 ? scalingStats : null,
            effectPct,
            suppressNotables,
            allocateDisconnected,
            ring,
            suppressAllocatedSmalls,
            copyUnallocatedSmalls,
            strengthMeleeBonus, tattooEffectPct, keystonesOnly);
    }

    /// <summary>Path of Building's <c>ScaleAddList</c> scales integer stats and truncates toward zero; 50% on 5
    /// therefore gives 7, not 8.</summary>
    public static int ScaleEffect(int value, int pct) =>
        (int)Math.Truncate(value * (100 + pct) / 100.0);

    /// <summary>Might in All Forms: the Dexterity plus Intelligence on a passive, counted toward the Strength melee
    /// damage bonus (1% per 5 points, the same rate Strength itself uses in <see cref="CharacterStats"/>).</summary>
    public static int MeleeAttributes(IReadOnlyList<(uint Key, int Value)> stats)
    {
        uint? dexterity = Stat.BaseDexterity;
        uint? intelligence = Stat.BaseIntelligence;
        int total = 0;
        foreach (var (key, value) in stats)
            if (key == dexterity || key == intelligence) total += value;
        return total;
    }

    // These families encode their weapon applicability in the stat ID itself. Only move a stat if both
    // rows exist in this client. An unrelated stat on the same passive retains its exact original key.
    static List<Move> MeleeToBow = default!;

    static List<Move> LoadMeleeToBow()
    {
        var result = new List<Move>();
        foreach (string weapon in new[] { "claw", "dagger", "sword", "axe", "mace", "staff" })
        {
            foreach (string suffix in new[] { "damage_+%", "hit_and_ailment_damage_+%", "ailment_damage_+%",
                "elemental_damage_+%", "attack_speed_+%", "accuracy_rating", "accuracy_rating_+%",
                "critical_strike_chance_+%", "critical_strike_multiplier_+", "stun_duration_+%",
                "stun_threshold_reduction_+%", "steal_power_frenzy_endurance_charges_on_hit_%" })
                AddMove(result, Stat.Of($"{weapon}_{suffix}"), Stat.Of($"bow_{suffix}"));
            AddMove(result, Stat.Of($"physical_{weapon}_damage_+%"), Stat.PhysicalBowDamagePlusPct);
            AddMove(result, Stat.Of($"global_critical_strike_chance_+%_while_holding_{weapon}"), Stat.GlobalCriticalStrikeChancePlusPctWhileHoldingBow);
            AddMove(result, Stat.Of($"global_critical_strike_multiplier_+_while_holding_{weapon}"), Stat.GlobalCriticalStrikeMultiplierPlusWhileHoldingBow);
            AddMove(result, Stat.Of($"with_{weapon}_additional_block_%"), Stat.WithBowAdditionalBlockPct);
            AddMove(result, Stat.Of($"spell_block_with_{weapon}_%"), Stat.SpellBlockWithBowPct);
            AddMove(result, Stat.Of($"knockback_on_crit_with_{weapon}"), Stat.KnockbackOnCritWithBow);
        }
        AddMove(result, Stat.MeleeDamagePlusPct, Stat.BowDamagePlusPct);
        AddMove(result, Stat.MeleePhysicalDamagePlusPct, Stat.PhysicalBowDamagePlusPct);
        AddMove(result, Stat.MeleeAttackSpeedPlusPct, Stat.BowAttackSpeedPlusPct);
        AddMove(result, Stat.MeleeCriticalStrikeChancePlusPct, Stat.BowCriticalStrikeChancePlusPct);
        foreach (var prefix in new[] { "one_handed_melee_", "two_handed_melee_" })
        {
            AddMove(result, Stat.Of(prefix + "physical_damage_+%"), Stat.PhysicalBowDamagePlusPct);
            foreach (var stat in new[] { "attack_speed_+%", "accuracy_rating_+%", "critical_strike_chance_+%",
                "critical_strike_multiplier_+", "stun_duration_+%" })
                AddMove(result, Stat.Of(prefix + stat), Stat.Of("bow_" + stat));
            AddMove(result, Stat.Of(prefix + "weapon_ailment_damage_+%"), Stat.BowAilmentDamagePlusPct);
            AddMove(result, Stat.Of(prefix + "weapon_hit_and_ailment_damage_+%"), Stat.BowHitAndAilmentDamagePlusPct);
        }
        AddMove(result, Stat.CriticalStrikeMultiplierWithDaggerPlus, Stat.BowCriticalStrikeMultiplierPlus);
        AddMove(result, Stat.MaceOrStaffHitAndAilmentDamagePlusPct, Stat.BowHitAndAilmentDamagePlusPct);
        AddMove(result, Stat.AxeOrSwordAilmentDamagePlusPct, Stat.BowAilmentDamagePlusPct);
        AddMove(result, Stat.MeleeWeaponAilmentDamagePlusPct, Stat.BowAilmentDamagePlusPct);
        AddMove(result, Stat.MeleeWeaponCriticalStrikeMultiplierPlus, Stat.BowCriticalStrikeMultiplierPlus);
        AddMove(result, Stat.MeleeAttacksUsableWithoutManaCost, Stat.BowAttacksUsableWithoutManaCost);
        AddMove(result, Stat.StaffBlockPct, Stat.WithBowAdditionalBlockPct);
        return result;
    }

    static void AddMove(List<Move> moves, uint from, uint to, int factor = 1)
    {
        moves.Add(new(from, to, factor));
    }

    static ResistanceAddition? MakeResistance(uint source, uint target, int scale)
    {
        return new([source, Stat.BaseResistAllElementsPct], target, scale <= 1 ? 100 : scale);
    }

    public static bool IsRadius(Item item, ModCatalog mods) => ReadConversion(item, mods) is not null;

    /// <summary>The stat list with every move applied. Cold Steel is a simultaneous swap; the other conversions
    /// remove each source and add its value (times the factor) to the target, merging an existing target.</summary>
    public static IReadOnlyList<(uint Key, int Value)> Convert(IReadOnlyList<(uint Key, int Value)> stats,
        Conversion conversion)
    {
        if (conversion.Swap is { } swap)
        {
            int sourceValue = 0, targetValue = 0;
            var swapped = new List<(uint Key, int Value)>(stats.Count);
            foreach (var (key, value) in stats)
            {
                if (key == swap.From) sourceValue += value;
                else if (key == swap.To) targetValue += value;
                else swapped.Add((key, value));
            }
            Add(swapped, swap.To, sourceValue);
            Add(swapped, swap.From, targetValue);
            return swapped;
        }

        var moved = new Dictionary<uint, int>();
        var result = new List<(uint Key, int Value)>(stats.Count + conversion.Moves.Count);
        foreach (var (key, value) in stats)
        {
            bool matched = false;
            foreach (var move in conversion.Moves)
                if (move.From == key)
                {
                    moved[move.To] = moved.GetValueOrDefault(move.To) + value * move.Factor;
                    matched = true;
                }
            if (!matched) result.Add((key, value));
        }
        foreach (var (target, value) in moved) Add(result, target, value);
        return result;
    }

    static void Add(List<(uint Key, int Value)> stats, uint key, int value)
    {
        if (value == 0) return;
        int at = stats.FindIndex(stat => stat.Key == key);
        if (at >= 0) stats[at] = (key, stats[at].Value + value);
        else stats.Add((key, value));
    }

    static ItemMod CreateMod(ModCatalog.Mod row, int? value = null, ModSection section = ModSection.Explicit)
    {
        var values = row.MaxValues().ToArray();
        if (value is int selected)
            for (int i = 0; i < row.Stats.Count; i++)
                if (row.Stats[i].Id == Stat.LocalJewelVariableRingRadiusValue) values[i] = selected;
        return new ItemMod(section, row.Hash, values);
    }

    /// <summary>Create a pilot radius jewel, max-rolled for predictable testing. Null when the name, base, a mod or the
    /// unique name word cannot be resolved.</summary>
    public static Item? Create(ItemCatalog items, string name, string folder = "data", string? variant = null) =>
        Find(name) is { } entry ? Create(items, entry, variant, folder) : null;

    public static Item? Create(ItemCatalog items, Entry entry, string? variant = null, string folder = "data")
    {
        if (items.Find(entry.Base) is not { } baseType) return null;
        if (UniqueJewelVisuals.Of(entry.Name) is not ushort visual) return null;
        if (WordHash(entry.Name, folder) is not uint word) return null;

        Variant? chosen = null;
        if (entry.Variants is { } variants)
        {
            chosen = variant is null
                ? variants[0]
                : variants.FirstOrDefault(v => v.Name.Equals(variant, StringComparison.OrdinalIgnoreCase))
                  ?? variants.FirstOrDefault(v => v.Name.Contains(variant, StringComparison.OrdinalIgnoreCase))
                  ?? (int.TryParse(variant, out int index) && index >= 1 && index <= variants.Length
                      ? variants[index - 1] : null);
            if (chosen is null) return null;
        }

        var mods = new List<ItemMod>();
        bool hasRadius = false;
        if (entry.VariantKeepsConversionMod && chosen is not null)
        {
            // Thread of Hope prints the chosen ring first, then the allocation rule and the resistance penalty.
            if (items.Mods.ById(chosen.ModId) is not { Stats.Count: > 0 } ring) return null;
            mods.Add(CreateMod(ring, chosen.Value, ModSection.Implicit));
            hasRadius |= ring.Stats.Any(stat => stat.Id == Stat.LocalJewelEffectBaseRadius);
            if (items.Mods.ById(entry.ConversionMod) is not { Stats.Count: > 0 } common) return null;
            // Thread of Hope's ring value supplies the radius; the allocate mod's 800 base would make the client
            // draw a filled disc instead of the annulus.
            var commonMod = CreateMod(common);
            var commonValues = commonMod.Values.ToArray();
            int baseRadiusAt = common.Stats.ToList().FindIndex(stat => stat.Id == Stat.LocalJewelEffectBaseRadius);
            if (baseRadiusAt >= 0 && baseRadiusAt < commonValues.Length) commonValues[baseRadiusAt] = 0;
            mods.Add(commonMod with { Values = commonValues });
            hasRadius |= common.Stats.Any(stat => stat.Id == Stat.LocalJewelEffectBaseRadius);
        }
        else
        {
            foreach (string globalId in entry.GlobalMods)
            {
                if (items.Mods.ById(globalId) is not { Stats.Count: > 0 } global) return null;
                mods.Add(CreateMod(global));
            }
            string conversionId = chosen?.ModId ?? entry.ConversionMod;
            if (items.Mods.ById(conversionId) is not { Stats.Count: > 0 } conversion) return null;
            mods.Add(CreateMod(conversion, chosen?.Value));
            hasRadius |= conversion.Stats.Any(stat => stat.Id == Stat.LocalJewelEffectBaseRadius);
        }
        if (entry.VariantKeepsConversionMod && chosen is not null)
            foreach (string globalId in entry.GlobalMods)
            {
                if (items.Mods.ById(globalId) is not { Stats.Count: > 0 } global) return null;
                mods.Add(CreateMod(global));
            }
        foreach (string extraId in entry.ExtraConversionMods ?? [])
        {
            if (items.Mods.ById(extraId) is not { Stats.Count: > 0 } extra) return null;
            mods.Add(CreateMod(extra));
            hasRadius |= extra.Stats.Any(stat => stat.Id == Stat.LocalJewelEffectBaseRadius);
        }
        if (!hasRadius && entry.Radius > 0)
        {
            if (items.Mods.ById("JewelEffectBaseRadiusUnique__1") is not { Stats.Count: > 0 } radius) return null;
            var radiusMod = CreateMod(radius);
            var values = radiusMod.Values.ToArray();
            int baseRadiusAt = radius.Stats.ToList().FindIndex(stat => stat.Id == Stat.LocalJewelEffectBaseRadius);
            if (baseRadiusAt >= 0 && baseRadiusAt < values.Length) values[baseRadiusAt] = entry.Radius;
            mods.Add(radiusMod with { Values = values });
        }

        return ItemCatalog.Create(baseType) with
        {
            Rarity = ItemRarity.Unique,
            Identified = true,
            ItemLevel = entry.Level,
            Corrupted = entry.Corrupted,
            VisualIdentity = visual,
            NameWords = [word],
            Mods = mods,
        };
    }

    static Dictionary<string, uint> NameWords = default!;

    public static uint? WordHash(string name, string folder) =>
        folder == "data" ? NameWords.GetValueOrDefault(name) : LoadNameWords(folder).GetValueOrDefault(name);

    static Dictionary<string, uint> LoadNameWords(string folder)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (!ClientTables.Has("Words")) return result;
        foreach (var row in ClientTables.Table("Words"))
            if (row.Int("Wordlist") == 6)
                result[row.Text("Text") ?? ""] = row.UInt("HASH32");
        return result;
    }
}
