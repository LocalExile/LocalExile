using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The pilot radius unique jewels: the six attribute-conversion jewels and the five damage/defence-conversion jewels.
/// Each item carries the client's local <c>local_unique_jewel_*</c> conversion mod (and, where the real item has one, a
/// global attribute/defence mod). The client draws the converted passives from the local mod; the server applies the
/// same stat move to the allocated passives in the item's radius. Lioneye's Fall is not in this catalogue yet: its
/// melee-to-bow change needs the weapon/melee modifier flags the server's stat model does not carry.
/// </summary>
static class RadiusJewelCatalogue
{
    public sealed record Variant(string Name, string ModId, int? Value = null);
    public sealed record Entry(string Name, string Base, string[] GlobalMods, string ConversionMod,
        int Limit = 0, byte Level = 1, bool Corrupted = false, int Radius = 0, Variant[]? Variants = null,
        string[]? ExtraConversionMods = null, bool VariantKeepsConversionMod = false);

    static readonly Variant[] RingVariants =
    [
        new("Small Ring", "JewelRingRadiusValuesUnique__1", 1),
        new("Medium Ring", "JewelRingRadiusValuesUnique__1", 2),
        new("Large Ring", "JewelRingRadiusValuesUnique__1", 3),
        new("Very Large Ring", "JewelRingRadiusValuesUnique__1", 4),
        new("Massive Ring", "JewelRingRadiusValuesUnique__2", 5),
    ];

    static readonly Variant[] NodeVariants =
    [
        new("Life", "UniqueJewelNodeIncreasedLifeUnique__1"),
        new("Energy Shield", "UniqueJewelNodeEnergyShieldUnique__1"),
        new("Mana", "UniqueJewelNodeIncreasedManaUnique__1"),
        new("Armour", "UniqueJewelNodeArmourUnique__1"),
        new("Evasion Rating", "UniqueJewelNodeEvasionUnique__1"),
        new("Attributes", "UniqueJewelNodeAllAttributesUnique__1"),
        new("Global Crit Chance", "UniqueJewelNodeCriticalStrikeChanceUnique__1"),
        new("Physical Damage", "UniqueJewelNodePhysicalDamageUnique__1"),
        new("Lightning Damage", "UniqueJewelNodeLightningDamageUnique__1"),
        new("Cold Damage", "UniqueJewelNodeColdDamageUnique__1"),
        new("Fire Damage", "UniqueJewelNodeFireDamageUnique__1"),
        new("Chaos Damage", "UniqueJewelNodeChaosDamageUnique__1"),
        new("Chaos Resistance", "UniqueJewelNodeChaosResistUnique__1"),
    ];

    internal static readonly Entry[] Seed =
    [
        new("Brute Force Solution", "Cobalt Jewel", ["IntelligenceUniqueJewel35"], "JewelStrToIntUniqueJewel35"),
        new("Careful Planning", "Viridian Jewel", ["DexterityUniqueJewel36"], "JewelIntToDexUniqueJewel36"),
        new("Efficient Training", "Crimson Jewel", ["StrengthUniqueJewel34"], "JewelIntToStrUniqueJewel34"),
        new("Fertile Mind", "Cobalt Jewel", ["IntelligenceUniqueJewel11"], "JewelDexToIntUniqueJewel11"),
        new("Fluid Motion", "Viridian Jewel", ["DexterityUniqueJewel13"], "JewelStrToDexUniqueJewel13"),
        new("Inertia", "Crimson Jewel", ["StrengthUniqueJewel37"], "JewelDexToStrUniqueJewel37"),
        new("Cold Steel", "Viridian Jewel", [], "ColdAndPhysicalNodesInRadiusSwapPropertiesUniqueJewel48_"),
        new("Fireborn", "Crimson Jewel", [], "AllDamageInRadiusBecomesFireUniqueJewel49"),
        new("Lioneye's Fall", "Viridian Jewel", [], "UniqueJewelMeleeToBow", 0, 1, false, 1200),
        new("Energy From Within", "Cobalt Jewel", ["IncreasedEnergyShieldPercentUniqueJewel51"],
            "LifeInRadiusBecomesEnergyShieldAtHalfValueUniqueJewel51"),
        new("Healthy Mind", "Cobalt Jewel", ["MaximumManaUniqueJewel54"],
            "LifePassivesBecomeManaPassivesInRadiusUniqueJewel54", 1),
        new("Energised Armour", "Crimson Jewel", ["IncreasedPhysicalDamageReductionRatingPercentUniqueJewel50"],
            "EnergyShieldInRadiusIncreasesArmourUniqueJewel50"),
        new("Fevered Mind", "Cobalt Jewel", ["IntelligenceUnique__21"],
            "NotablesGrantManaCostAndSpellDamageUnique1", 1, 1, true),
        new("Fortress Covenant", "Cobalt Jewel",
            ["MinionDamageUnique__6", "MinionAttackBlockChanceUnique__1", "MinionSpellBlockChanceUnique__1_"],
            "NotablesGrantMinionDamageTakenUnique__1_", 1, 20, true),
        new("Quickening Covenant", "Viridian Jewel",
            ["MinionAttackAndCastSpeedUnique__1", "MinionSuppressSpellChanceUnique__1"],
            "NotablesGrantMinionMovementSpeedUnique__1_", 1, 20, true),
        new("Coated Shrapnel", "Crimson Jewel", ["TrapPoisonChanceUnique__1"],
            "PassivesGrantTrapMineAddedPhysicalUnique__1_", 0, 1, false, 800),
        new("Martial Artistry", "Crimson Jewel", ["UnarmedStrikeRangeUniqueJewel__1_"],
            "PassivesGrantUnarmedAttackSpeedUnique__1_", 1, 1, false, 800),
        new("The Blue Dream", "Cobalt Jewel", ["ChaosDamageAsPortionOfLightningDamageUnique__1"],
            "LightningResistAlsoGrantsPowerChargeOnKillJewelUnique__1", 0, 1, false, 1500),
        new("The Blue Nightmare", "Cobalt Jewel", ["ChaosDamageAsPortionOfLightningDamageUnique__1"],
            "LightningResistConvertedToSpellBlockChanceScaledJewelUnique__1", 1, 1, false, 1500),
        new("The Green Dream", "Viridian Jewel", ["ChaosDamageAsPortionOfColdDamageUnique__1"],
            "ColdResistAlsoGrantsFrenzyChargeOnKillJewelUnique__1", 0, 1, false, 1500),
        new("The Green Nightmare", "Viridian Jewel", ["ChaosDamageAsPortionOfColdDamageUnique__1"],
            "ColdResistConvertedToDodgeChanceScaledJewelUnique__1", 1, 1, false, 1500),
        new("The Red Dream", "Crimson Jewel", ["ChaosDamageAsPortionOfFireDamageUnique__1"],
            "FireResistAlsoGrantsEnduranceChargeOnKillJewelUnique__1", 0, 1, false, 1500),
        new("The Red Nightmare", "Crimson Jewel", ["ChaosDamageAsPortionOfFireDamageUnique__1"],
            "FireResistConvertedToBlockChanceScaledJewelUnique__1_", 1, 1, false, 1500),
        new("The Light of Meaning", "Prismatic Jewel", [], NodeVariants[0].ModId, 1, 1, false, 1500, NodeVariants),
        new("The Perandus Pact", "Prismatic Jewel", [], NodeVariants[0].ModId, 1, 1, false, 1500, NodeVariants),
        new("Anatomical Knowledge", "Cobalt Jewel", ["MaximumLifeUniqueJewel52"],
            "LifePerIntelligenceInRadusUniqueJewel52", 0, 1, false, 1500),
        new("Eldritch Knowledge", "Cobalt Jewel", [], "ChaosDamageIncreasedPerIntUniqueJewel2", 0, 1, false, 1200),
        new("Pugilist", "Viridian Jewel", [], "ClawPhysDamageAndEvasionPerDexUniqueJewel47", 0, 1, false, 1500),
        new("Spire of Stone", "Crimson Jewel", ["TotemsCannotBeStunnedUniqueJewel15"],
            "TotemLifePerStrengthUniqueJewel15", 1, 1, false, 1500),
        new("Static Electricity", "Viridian Jewel", [], "AddedLightningDamagePerDexInRadiusUniqueJewel53", 0, 1, false, 1500),
        new("Tempered Flesh", "Crimson Jewel", [], "AdditionalStrengthPerAllocatedStrengthJewelUnique__1_",
            0, 1, false, 1200, null, ["LifeRecoveryRatePerAllocatedStrengthUnique__1_"]),
        new("Tempered Mind", "Cobalt Jewel", [], "AdditionalIntelligencePerAllocatedIntelligenceJewelUnique__1__",
            0, 1, false, 1200, null, ["ManaRecoveryRatePerAllocatedIntelligenceUnique__1"]),
        new("Tempered Spirit", "Viridian Jewel", [], "AdditionalDexterityPerAllocatedDexterityJewelUnique__1",
            0, 1, false, 1200, null, ["MovementSpeedPerAllocatedDexterityJewelUnique__1"]),
        new("Transcendent Flesh", "Crimson Jewel", [], "AdditionalStrengthPerAllocatedStrengthJewelUnique__1_",
            0, 1, false, 1200, null,
            ["CriticalStrikeMultiplierPerUnallocatedStrengthJewelUnique__1_",
             "LifeRecoveryRatePerAllocatedStrengthUnique__2", "LifeRecoveryRatePerUnallocatedStrengthUnique__1_"]),
        new("Transcendent Mind", "Cobalt Jewel", [], "AdditionalIntelligencePerAllocatedIntelligenceJewelUnique__1__",
            0, 1, false, 1200, null,
            ["DamageOverTimeMultiplierPerUnallocatedIntelligenceUnique__1___",
             "ManaRecoveryRatePerAllocatedIntelligenceUnique__2", "ManaRecoveryRatePerUnallocatedIntelligenceUnique__1"]),
        new("Transcendent Spirit", "Viridian Jewel", [], "AdditionalDexterityPerAllocatedDexterityJewelUnique__1",
            0, 1, false, 1200, null,
            ["MovementSpeedPerAllocatedDexterityUnique__2", "AccuracyRatingPerUnallocatedDexterityUnique__1_",
             "MovementSpeedPerUnallocatedDexterityUnique__1_"]),
        new("Might of the Meek", "Crimson Jewel", [], "PassiveEffectivenessJewelUnique__1_", 0, 20, false, 1500),
        new("Might in All Forms", "Crimson Jewel", [],
            "DexterityAndIntelligenceGiveStrengthMeleeBonusInRadiusUniqueJewel55", 0, 1, false, 1200),
        new("Pitch Darkness", "Viridian Jewel", [], "BurningArrowThresholdJewelUnique__1", 0, 1, false, 1200,
            null, ["FireDamagePercentUnique__9"]),
        new("Sudden Ignition", "Viridian Jewel", [], "BurningArrowThresholdJewel_2", 1, 1, false, 1200,
            null, ["FireDamagePercentUnique__10"]),
        new("Intuitive Leap", "Viridian Jewel", [], "JewelUniqueAllocateDisconnectedPassives", 0, 1, false, 800),
        new("Thread of Hope", "Crimson Jewel", ["AllResistancesUnique__6"],
            "JewelUniqueAllocateDisconnectedPassives", 0, 1, false, 800, RingVariants, null, true),
        new("Unnatural Instinct", "Viridian Jewel", [], "GrantsStatsFromNonNotablesInRadiusUnique__1", 1, 1, false, 800,
            null, ["AllocatedNonNotablesGrantNothingUnique__1_"]),
    ];

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
        bool StrengthMeleeBonus = false)
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
        conversion.AllocateDisconnected && !(conversion.Ring > 0 && node.IsJewelSocket);

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

    static readonly IReadOnlyDictionary<string, (string From, string To)> AttributePairs =
        new Dictionary<string, (string From, string To)>
        {
            ["str_to_int"] = ("base_strength", "base_intelligence"),
            ["int_to_dex"] = ("base_intelligence", "base_dexterity"),
            ["int_to_str"] = ("base_intelligence", "base_strength"),
            ["dex_to_int"] = ("base_dexterity", "base_intelligence"),
            ["str_to_dex"] = ("base_strength", "base_dexterity"),
            ["dex_to_str"] = ("base_dexterity", "base_strength"),
        };

    // local stat id -> target stat, count stat, per divisor, whether the value is the divisor, allocated/unallocated.
    static readonly IReadOnlyDictionary<string, (string Target, string Count, int Per, bool Divisor, bool Allocated)>
        ScalingRules = new Dictionary<string, (string Target, string Count, int Per, bool Divisor, bool Allocated)>
        {
            ["local_unique_jewel_additional_life_per_X_int_in_radius"] =
                ("base_maximum_life", "base_intelligence", 0, true, true),
            ["local_unique_jewel_chaos_damage_+%_per_10_int_in_radius"] =
                ("chaos_damage_+%", "base_intelligence", 10, false, true),
            ["local_unique_jewel_evasion_rating_+%_per_X_dex_in_radius"] =
                ("evasion_rating_+%", "base_dexterity", 0, true, true),
            ["local_unique_jewel_claw_physical_damage_+%_per_X_dex_in_radius"] =
                ("physical_claw_damage_+%", "base_dexterity", 0, true, true),
            ["local_unique_jewel_unarmed_damage_+%_per_X_dex_in_radius"] =
                ("unarmed_melee_physical_damage_+%", "base_dexterity", 0, true, true),
            ["local_unique_jewel_totem_life_+X%_per_10_str_in_radius"] =
                ("totem_life_+%", "base_strength", 10, false, true),
            ["local_unique_jewel_one_additional_maximum_lightning_damage_per_X_dex"] =
                ("attack_maximum_added_lightning_damage", "base_dexterity", 0, true, true),
            ["local_unique_jewel_X_strength_per_1_strength_allocated_in_radius"] =
                ("base_strength", "base_strength", 1, false, true),
            ["local_unique_jewel_X_intelligence_per_1_intelligence_allocated_in_radius"] =
                ("base_intelligence", "base_intelligence", 1, false, true),
            ["local_unique_jewel_X_dexterity_per_1_dexterity_allocated_in_radius"] =
                ("base_dexterity", "base_dexterity", 1, false, true),
            ["local_unique_jewel_critical_strike_multiplier_+_per_10_str_unallocated_in_radius"] =
                ("base_critical_strike_multiplier_+", "base_strength", 10, false, false),
            ["local_unique_jewel_life_recovery_rate_+%_per_10_str_allocated_in_radius"] =
                ("life_recovery_rate_+%", "base_strength", 10, false, true),
            ["local_unique_jewel_life_recovery_rate_+%_per_10_str_unallocated_in_radius"] =
                ("life_recovery_rate_+%", "base_strength", 10, false, false),
            ["local_unique_jewel_additional_physical_damage_reduction_%_per_10_str_allocated_in_radius"] =
                ("additional_physical_damage_reduction_%", "base_strength", 10, false, true),
            ["local_unique_jewel_accuracy_rating_+_per_10_int_unallocated_in_radius"] =
                ("accuracy_rating", "base_intelligence", 10, false, false),
            ["local_unique_jewel_mana_recovery_rate_+%_per_10_int_allocated_in_radius"] =
                ("mana_recovery_rate_+%", "base_intelligence", 10, false, true),
            ["local_unique_jewel_mana_recovery_rate_+%_per_10_int_unallocated_in_radius"] =
                ("mana_recovery_rate_+%", "base_intelligence", 10, false, false),
            ["local_unique_jewel_energy_shield_regeneration_rate_per_minute_%_per_10_int_allocated_in_radius"] =
                ("base_energy_shield_regeneration_rate_per_minute_%", "base_intelligence", 10, false, true),
            ["local_unique_jewel_dot_multiplier_+_per_10_int_unallocated_in_radius"] =
                ("dot_multiplier_+", "base_intelligence", 10, false, false),
            ["local_unique_jewel_maximum_mana_+_per_10_dex_unallocated_in_radius"] =
                ("base_maximum_mana", "base_dexterity", 10, false, false),
            ["local_unique_jewel_movement_speed_+%_per_10_dex_allocated_in_radius"] =
                ("base_movement_velocity_+%", "base_dexterity", 10, false, true),
            ["local_unique_jewel_movement_speed_+%_per_10_dex_unallocated_in_radius"] =
                ("base_movement_velocity_+%", "base_dexterity", 10, false, false),
            ["local_unique_jewel_accuracy_rating_+_per_10_dex_unallocated_in_radius"] =
                ("accuracy_rating", "base_dexterity", 10, false, false),
        };

    public static Conversion? ReadConversion(Item item, ModCatalog mods)
    {
        var moves = new List<Move>();
        var notableStats = new List<(uint Key, int Value)>();
        var additionStats = new List<(uint Key, int Value)>();
        var scalingStats = new List<ScalingStat>();
        ResistanceAddition? resistance = null;
        int radius = 0, effectPct = 0, ring = 0;
        bool physicalToCold = false, coldToPhysical = false, suppressNotables = false;
        bool allocateDisconnected = false, suppressAllocatedSmalls = false, copyUnallocatedSmalls = false;
        bool strengthMeleeBonus = false;
        foreach (var mod in item.Mods)
        {
            if (mods.ByHash(mod.Hash) is not { } row) continue;
            for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
            {
                string id = row.Stats[i].Id;
                if (id == "local_jewel_effect_base_radius")
                {
                    radius = mod.Values[i];
                    continue;
                }
                if (id.StartsWith("local_jewel_nearby_passives_", StringComparison.Ordinal))
                {
                    if (AttributePairs.TryGetValue(id["local_jewel_nearby_passives_".Length..], out var pair))
                        AddMove(moves, pair.From, pair.To);
                    continue;
                }
                if (id.StartsWith("local_unique_jewel_notable_passives_in_radius_instead_grant_",
                    StringComparison.Ordinal))
                {
                    string target = id["local_unique_jewel_notable_passives_in_radius_instead_grant_".Length..];
                    if (Program.Stats.Key(target) is uint key) notableStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (id.StartsWith("local_unique_jewel_passives_in_radius_grant_", StringComparison.Ordinal))
                {
                    string target = id["local_unique_jewel_passives_in_radius_grant_".Length..];
                    if (Program.Stats.Key(target) is uint key) additionStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (id.StartsWith("local_unique_jewel_passives_in_radius_give_", StringComparison.Ordinal))
                {
                    string target = id["local_unique_jewel_passives_in_radius_give_".Length..];
                    if (Program.Stats.Key(target) is uint key) additionStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (id == "local_unique_jewel_skills_in_radius_grant_%_unarmed_melee_attack_speed")
                {
                    if (Program.Stats.Key("unarmed_melee_attack_speed_+%") is uint key)
                        additionStats.Add((key, mod.Values[i]));
                    continue;
                }
                if (id == "local_unique_jewel_lightning_resistance_also_grants_power_charge_on_kill_chance")
                {
                    resistance = MakeResistance("base_lightning_damage_resistance_%", "add_power_charge_on_kill_%_chance", 100);
                    continue;
                }
                if (id == "local_unique_jewel_lightning_resistance_also_grants_block_spells_chance_scaled_%")
                {
                    resistance = MakeResistance("base_lightning_damage_resistance_%", "additional_spell_block_%", mod.Values[i]);
                    continue;
                }
                if (id == "local_unique_jewel_cold_resistance_also_grants_frenzy_charge_on_kill_chance")
                {
                    resistance = MakeResistance("base_cold_damage_resistance_%", "add_frenzy_charge_on_kill_%_chance", 100);
                    continue;
                }
                if (id == "local_unique_jewel_cold_resistance_also_grants_spell_suppression_chance_scaled_%")
                {
                    resistance = MakeResistance("base_cold_damage_resistance_%", "base_spell_suppression_chance_%", mod.Values[i]);
                    continue;
                }
                if (id == "local_unique_jewel_fire_resistance_also_grants_endurance_charge_on_kill_chance")
                {
                    resistance = MakeResistance("base_fire_damage_resistance_%", "endurance_charge_on_kill_%", 100);
                    continue;
                }
                if (id == "local_unique_jewel_fire_resistance_also_grants_block_chance_scaled_%")
                {
                    resistance = MakeResistance("base_fire_damage_resistance_%", "additional_block_%", mod.Values[i]);
                    continue;
                }
                if (id == "local_unique_jewel_non_keystone_passive_in_radius_effect_+%")
                {
                    effectPct += mod.Values[i];
                    continue;
                }
                if (id == "local_unique_jewel_notable_passive_in_radius_does_nothing")
                {
                    if (mod.Values[i] != 0) suppressNotables = true;
                    continue;
                }
                if (id == "local_unique_jewel_nearby_disconnected_passives_can_be_allocated")
                {
                    if (mod.Values[i] != 0) allocateDisconnected = true;
                    continue;
                }
                if (id == "local_jewel_variable_ring_radius_value")
                {
                    ring = mod.Values[i];
                    continue;
                }
                if (id == "local_jewel_allocated_non_notable_passives_in_radius_grant_nothing")
                {
                    if (mod.Values[i] != 0) suppressAllocatedSmalls = true;
                    continue;
                }
                if (id == "local_jewel_copy_stats_from_unallocated_non_notable_passives_in_radius")
                {
                    if (mod.Values[i] != 0) copyUnallocatedSmalls = true;
                    continue;
                }
                if (id == "local_unique_jewel_dex_and_int_apply_to_str_melee_damage_bonus_in_radius")
                {
                    strengthMeleeBonus = true;
                    continue;
                }
                if (ScalingRules.TryGetValue(id, out var rule))
                {
                    if (Program.Stats.Key(rule.Target) is uint target && Program.Stats.Key(rule.Count) is uint count)
                        scalingStats.Add(new(target, count, rule.Per, mod.Values[i], rule.Divisor, rule.Allocated));
                    continue;
                }
                switch (id)
                {
                    case "local_unique_jewel_physical_damage_increases_applies_to_cold_damage":
                        physicalToCold = true;
                        break;
                    case "local_unique_jewel_cold_damage_increases_applies_to_physical_damage":
                        coldToPhysical = true;
                        break;
                    case "local_unique_jewel_damage_increases_applies_to_fire_damage":
                        AddMove(moves, "physical_damage_+%", "fire_damage_+%");
                        AddMove(moves, "cold_damage_+%", "fire_damage_+%");
                        AddMove(moves, "lightning_damage_+%", "fire_damage_+%");
                        AddMove(moves, "chaos_damage_+%", "fire_damage_+%");
                        AddMove(moves, "elemental_damage_+%", "fire_damage_+%");
                        break;
                    case "local_unique_jewel_life_increases_applies_to_energy_shield":
                        AddMove(moves, "maximum_life_+%", "maximum_energy_shield_+%");
                        break;
                    case "local_unique_jewel_life_increases_applies_to_mana_doubled":
                        AddMove(moves, "maximum_life_+%", "maximum_mana_+%", 2);
                        break;
                    case "local_unique_jewel_energy_shield_increases_applies_to_armour_doubled":
                        AddMove(moves, "maximum_energy_shield_+%", "physical_damage_reduction_rating_+%", 2);
                        break;
                    case "local_unique_jewel_melee_applies_to_bow":
                        // Needs melee/weapon flags on each modifier, not just a stat-key move; not in this pilot.
                        return null;
                }
            }
        }
        if (radius == 0 && EntryOf(item, mods) is { Radius: > 0 } entry) radius = entry.Radius;
        if (radius <= 0) return null;
        (uint From, uint To)? swap = null;
        if (physicalToCold && coldToPhysical)
            swap = (Program.Stats.Key("physical_damage_+%")!.Value, Program.Stats.Key("cold_damage_+%")!.Value);
        else
        {
            if (physicalToCold) AddMove(moves, "physical_damage_+%", "cold_damage_+%");
            if (coldToPhysical) AddMove(moves, "cold_damage_+%", "physical_damage_+%");
        }
        if (moves.Count == 0 && swap is null && notableStats.Count == 0 && additionStats.Count == 0
            && resistance is null && scalingStats.Count == 0 && effectPct == 0 && !suppressNotables
            && !allocateDisconnected && ring == 0 && !suppressAllocatedSmalls && !copyUnallocatedSmalls
            && !strengthMeleeBonus)
            return null;
        return new(moves, swap, radius,
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
            strengthMeleeBonus);
    }

    /// <summary>Path of Building's <c>ScaleAddList</c> scales integer stats and truncates toward zero; 50% on 5
    /// therefore gives 7, not 8.</summary>
    internal static int ScaleEffect(int value, int pct) =>
        (int)Math.Truncate(value * (100 + pct) / 100.0);

    /// <summary>Might in All Forms: the Dexterity plus Intelligence on a passive, counted toward the Strength melee
    /// damage bonus (1% per 5 points, the same rate Strength itself uses in <see cref="CharacterStats"/>).</summary>
    public static int MeleeAttributes(IReadOnlyList<(uint Key, int Value)> stats)
    {
        uint? dexterity = Program.Stats.Key("base_dexterity");
        uint? intelligence = Program.Stats.Key("base_intelligence");
        int total = 0;
        foreach (var (key, value) in stats)
            if (key == dexterity || key == intelligence) total += value;
        return total;
    }

    static void AddMove(List<Move> moves, string from, string to, int factor = 1)
    {
        if (Program.Stats.Key(from) is not uint source || Program.Stats.Key(to) is not uint target) return;
        moves.Add(new(source, target, factor));
    }

    static ResistanceAddition? MakeResistance(string source, string target, int scale)
    {
        if (Program.Stats.Key(source) is not uint sourceKey
            || Program.Stats.Key("base_resist_all_elements_%") is not uint allKey
            || Program.Stats.Key(target) is not uint targetKey) return null;
        return new([sourceKey, allKey], targetKey, scale <= 1 ? 100 : scale);
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
                if (row.Stats[i].Id == "local_jewel_variable_ring_radius_value") values[i] = selected;
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
            hasRadius |= ring.Stats.Any(stat => stat.Id == "local_jewel_effect_base_radius");
            if (items.Mods.ById(entry.ConversionMod) is not { Stats.Count: > 0 } common) return null;
            // Thread of Hope's ring value supplies the radius; the allocate mod's 800 base would make the client
            // draw a filled disc instead of the annulus.
            var commonMod = CreateMod(common);
            var commonValues = commonMod.Values.ToArray();
            int baseRadiusAt = common.Stats.ToList().FindIndex(stat => stat.Id == "local_jewel_effect_base_radius");
            if (baseRadiusAt >= 0 && baseRadiusAt < commonValues.Length) commonValues[baseRadiusAt] = 0;
            mods.Add(commonMod with { Values = commonValues });
            hasRadius |= common.Stats.Any(stat => stat.Id == "local_jewel_effect_base_radius");
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
            hasRadius |= conversion.Stats.Any(stat => stat.Id == "local_jewel_effect_base_radius");
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
            hasRadius |= extra.Stats.Any(stat => stat.Id == "local_jewel_effect_base_radius");
        }
        if (!hasRadius && entry.Radius > 0)
        {
            if (items.Mods.ById("JewelEffectBaseRadiusUnique__1") is not { Stats.Count: > 0 } radius) return null;
            var radiusMod = CreateMod(radius);
            var values = radiusMod.Values.ToArray();
            int baseRadiusAt = radius.Stats.ToList().FindIndex(stat => stat.Id == "local_jewel_effect_base_radius");
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

    static readonly Lazy<Dictionary<string, uint>> NameWords = new(() => LoadNameWords("data"));

    internal static uint? WordHash(string name, string folder) =>
        folder == "data" ? NameWords.Value.GetValueOrDefault(name) : LoadNameWords(folder).GetValueOrDefault(name);

    static Dictionary<string, uint> LoadNameWords(string folder)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(folder, "tables", "Words.json");
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var row in d.RootElement.EnumerateArray())
            if (row.GetProperty("Wordlist").GetInt32() == 6)
                result[row.GetProperty("Text").GetString() ?? ""] = row.GetProperty("HASH32").GetUInt32();
        return result;
    }
}
