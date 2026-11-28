using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>
/// Eldritch Altars (<c>TangleAltar</c> / <c>CleansingFireAltar</c>):
/// choice table loading from <c>AtlasPrimordialAltarChoices</c> (267 rows) &amp; <c>Mods</c>,
/// altar spawning on Eldritch pack kills with Atlas passive modifiers (<c>Eldritch Gaze</c>,
/// <c>Wrath of the Cosmos</c>, <c>The Shadow of Hunger</c>, <c>The Light of Dawn</c>),
/// <c>0x02bb</c> <c>BaseData</c> (<c>elemType = 8</c>, 12 exact <c>u32 LE</c> hashes),
/// <c>C→S 0x028e</c> selection handling, <c>S→C 0x02c9</c> Ruleset 99 HUD updates, and live
/// player / Eldritch minion / map boss combat &amp; loot stat application.
/// </summary>
public static class EldritchAltars
{
    public const int TargetPlayer = 0;
    public const int TargetInfluencedMonsters = 1;
    public const int TargetMapBoss = 2;

    const int MaxAltarsPerMap = 8;
    const int BasePackAltarSpawnChancePct = 24;

    public sealed record AltarModDef(
        int ChoiceRow,
        int ModRow,
        string ModId,
        ushort Hash16,
        uint Hash32,
        PrimordialBoss Boss,
        int TargetType,
        bool IsUpside,
        bool HardMode,
        int Weight,
        IReadOnlyList<(uint StatId, int Min, int Max)> Stats);

    public sealed record RolledMod(
        AltarModDef Def,
        int[] StatValues)
    {
        public InstanceRulesets.PrimordialAltarMod ToHudMod() => new(Def.Hash16, StatValues);
    }

    public sealed record AltarOption(
        int TargetType,
        IReadOnlyList<RolledMod> Downsides,
        IReadOnlyList<RolledMod> Upsides);

    public sealed class SpawnedAltar
    {
        public required uint EntityId { get; init; }
        public required PrimordialBoss Boss { get; init; }
        public required string MetadataPath { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required AltarOption Choice1 { get; init; }
        public required AltarOption Choice2 { get; init; }
        public bool Activated;

        public PrimordialAltarObject ToWireObject() =>
            new(X, Y, BuildBaseDataEntries(Choice1, Choice2), Activated: Activated, Doodads: true, Awake: true);
    }

    static List<AltarModDef> _allChoices = [];
    static Dictionary<(PrimordialBoss Boss, int TargetType, bool IsUpside, bool HardMode), List<AltarModDef>> _pools = [];

    // Stat wire id -> exact BaseItemTypes Id for currency & Eldritch currency altar drop upsides.
    static Dictionary<uint, string> _currencyDropStatToItem = [];
    // Stat wire id -> pre-cached matching Scarab BaseItemTypes Ids for scarab drop upsides.
    static Dictionary<uint, string[]> _scarabDropStatToItems = [];
    // Stat wire id -> divination card drop upside marker.
    static HashSet<uint> _divCardMinionDropStats = [];
    static HashSet<uint> _divCardBossDropStats = [];
    static Dictionary<uint, string> _statIdToName = [];

    public static IReadOnlyList<AltarModDef> AllChoices => _allChoices;
    public static IReadOnlyDictionary<uint, string> CurrencyDropStatToItem => _currencyDropStatToItem;

    /// <summary>Loads all 267 <c>AtlasPrimordialAltarChoices</c> rows joined with <c>Mods</c> from <c>client.db</c>.</summary>
    public static void LoadData()
    {
        var choices = new List<AltarModDef>();
        var pools = new Dictionary<(PrimordialBoss, int, bool, bool), List<AltarModDef>>();
        var statIdToName = new Dictionary<uint, string>();

        if (ClientTables.Has("Stats"))
        {
            int idx = 0;
            foreach (var s in ClientTables.Table("Stats"))
                statIdToName[(uint)(++idx)] = s.Text("Id") ?? "";
        }
        _statIdToName = statIdToName;

        int tangleTag = EldritchInfluence.Bosses.TryGetValue(PrimordialBoss.Tangle, out var tb) ? tb.AltarTagRow : 962;
        int cleansingTag = EldritchInfluence.Bosses.TryGetValue(PrimordialBoss.CleansingFire, out var cb) ? cb.AltarTagRow : 961;

        if (ClientTables.Has("AtlasPrimordialAltarChoices") && ClientTables.Has("Mods"))
        {
            var modsTable = ClientTables.Table("Mods");
            int choiceIdx = 0;
            foreach (var row in ClientTables.Table("AtlasPrimordialAltarChoices"))
            {
                int cRow = choiceIdx++;
                if (row.IntOrNull("Mod") is not int modRow || modRow < 0 || modRow >= modsTable.Count) continue;
                int targetType = row.IntOrNull("Type") ?? -1;
                if (targetType is < 0 or > 2) continue;

                var mod = modsTable[modRow];
                string modId = mod.Text("Id") ?? "";
                if (modId.Length == 0) continue;

                ushort hash16 = (ushort)(mod.IntOrNull("HASH16") ?? 0);
                uint hash32 = mod.UIntOrNull("HASH32") ?? 0;
                if (hash32 == 0) continue;

                bool hardMode = modId.StartsWith("HardMode", StringComparison.OrdinalIgnoreCase);
                bool isUpside = modId.Contains("Upside", StringComparison.OrdinalIgnoreCase)
                    || (!modId.Contains("Downside", StringComparison.OrdinalIgnoreCase) && (row.IntOrNull("__col2") ?? 0) != 0);

                // Resolve boss & spawn weight from SpawnWeight_TagsKeys / SpawnWeight_Values (fallback to Id prefix).
                int[] tagKeys = [.. mod.Ints("SpawnWeight_TagsKeys")];
                int[] tagVals = [.. mod.Ints("SpawnWeight_Values")];
                int tangleW = -1, cleansingW = -1;
                for (int i = 0; i < Math.Min(tagKeys.Length, tagVals.Length); i++)
                {
                    if (tagKeys[i] == tangleTag) tangleW = tagVals[i];
                    else if (tagKeys[i] == cleansingTag) cleansingW = tagVals[i];
                }

                PrimordialBoss boss;
                int weight;
                if (modId.Contains("Tangle", StringComparison.OrdinalIgnoreCase))
                {
                    boss = PrimordialBoss.Tangle;
                    weight = tangleW >= 0 ? tangleW : 1000;
                }
                else if (modId.Contains("Cleansing", StringComparison.OrdinalIgnoreCase))
                {
                    boss = PrimordialBoss.CleansingFire;
                    weight = cleansingW >= 0 ? cleansingW : 1000;
                }
                else if (tangleW > 0 && cleansingW <= 0)
                {
                    boss = PrimordialBoss.Tangle;
                    weight = tangleW;
                }
                else if (cleansingW > 0 && tangleW <= 0)
                {
                    boss = PrimordialBoss.CleansingFire;
                    weight = cleansingW;
                }
                else continue;

                // Collect ordered non-null stat definitions (StatsKey1..StatsKey8, wire id = _row + 1).
                var stats = new List<(uint StatId, int Min, int Max)>(4);
                for (int slot = 1; slot <= 8; slot++)
                {
                    string keyCol = $"StatsKey{slot}";
                    if (!mod.Has(keyCol) || mod.IntOrNull(keyCol) is not int statRow || statRow < 0) continue;
                    int min = mod.IntOrNull($"Stat{slot}Min") ?? 0;
                    int max = mod.IntOrNull($"Stat{slot}Max") ?? min;
                    if (max < min) (min, max) = (max, min);
                    stats.Add(((uint)(statRow + 1), min, max));
                }

                var def = new AltarModDef(cRow, modRow, modId, hash16, hash32, boss, targetType, isUpside, hardMode, weight, stats);
                choices.Add(def);
                if (weight > 0)
                {
                    var poolKey = (boss, targetType, isUpside, hardMode);
                    if (!pools.TryGetValue(poolKey, out var list))
                        pools[poolKey] = list = [];
                    list.Add(def);
                }
            }
        }

        _allChoices = choices;
        _pools = pools;
        BuildDropStatMaps(statIdToName);
    }

    static void BuildDropStatMaps(IReadOnlyDictionary<uint, string> statIdToName)
    {
        var currencyMap = new Dictionary<uint, string>();
        var scarabMap = new Dictionary<uint, string>();
        var divMinion = new HashSet<uint>();
        var divBoss = new HashSet<uint>();

        var knownCurrencySuffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["glassblowers_bauble"] = "Metadata/Items/Currency/CurrencyFlaskQuality",
            ["gemcutters_prism"] = "Metadata/Items/Currency/CurrencyGemQuality",
            ["chaos_orb"] = "Metadata/Items/Currency/CurrencyRerollRare",
            ["orb_of_regret"] = "Metadata/Items/Currency/CurrencyPassiveRefund",
            ["orb_of_alteration"] = "Metadata/Items/Currency/CurrencyRerollMagic",
            ["orb_of_scouring"] = "Metadata/Items/Currency/CurrencyConvertToNormal",
            ["exalted_orb"] = "Metadata/Items/Currency/CurrencyAddModToRare",
            ["regal_orb"] = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare",
            ["chromatic_orb"] = "Metadata/Items/Currency/CurrencyRerollSocketColours",
            ["fusing_orb"] = "Metadata/Items/Currency/CurrencyRerollSocketLinks",
            ["jewellers_orb"] = "Metadata/Items/Currency/CurrencyRerollSocketNumbers",
            ["divine_orb"] = "Metadata/Items/Currency/CurrencyModValues",
            ["blessed_orb"] = "Metadata/Items/Currency/CurrencyRerollImplicit",
            ["vaal_orb"] = "Metadata/Items/Currency/CurrencyCorrupt",
            ["orb_of_annulment"] = "Metadata/Items/Currency/CurrencyRemoveMod",
            ["orb_of_binding"] = "Metadata/Items/Currency/CurrencyUpgradeToRareAndSetSockets",
            ["orb_of_unmaking"] = "Metadata/Items/Currency/CurrencyAtlasPassiveRefund",
            ["enkindling_orb"] = "Metadata/Items/Currency/CurrencyEnkindlingOrb",
            ["instilling_orb"] = "Metadata/Items/Currency/CurrencyInstillingOrb",
            ["cartographers_chisel"] = "Metadata/Items/Currency/CurrencyMapQuality",
            ["orb_of_alchemy"] = "Metadata/Items/Currency/CurrencyUpgradeToRare",
            ["orb_of_chance"] = "Metadata/Items/Currency/CurrencyUpgradeRandomly",
            ["orb_of_horizons"] = "Metadata/Items/Currency/CurrencyRerollMapType",
            ["lesser_eldritch_ember"] = "Metadata/Items/Currency/CurrencyEldritchEmber1",
            ["greater_eldritch_ember"] = "Metadata/Items/Currency/CurrencyEldritchEmber2",
            ["grand_eldritch_ember"] = "Metadata/Items/Currency/CurrencyEldritchEmber3",
            ["exceptional_eldritch_ember"] = "Metadata/Items/Currency/CurrencyEldritchEmber4",
            ["lesser_eldritch_ichor"] = "Metadata/Items/Currency/CurrencyEldritchIchor1",
            ["greater_eldritch_ichor"] = "Metadata/Items/Currency/CurrencyEldritchIchor2",
            ["grand_eldritch_ichor"] = "Metadata/Items/Currency/CurrencyEldritchIchor3",
            ["exceptional_eldritch_ichor"] = "Metadata/Items/Currency/CurrencyEldritchIchor4",
            ["eldritch_chaos_orb"] = "Metadata/Items/Currency/CurrencyEldritchRerollRare",
            ["eldritch_exalted_orb"] = "Metadata/Items/Currency/CurrencyEldritchAddModToRare",
            ["eldritch_orb_of_annulment"] = "Metadata/Items/Currency/CurrencyEldritchRemoveMod",
        };

        var scarabCategoryPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["influence"] = "ScarabInfluence",
            ["sulphite"] = "ScarabSulphite",
            ["breach"] = "ScarabBreach",
            ["maps"] = "ScarabMaps",
            ["divination"] = "ScarabDivination",
            ["anarchy"] = "ScarabAnarchy",
            ["misc"] = "ScarabMisc",
            ["delirium"] = "ScarabDelirium",
            ["beyond"] = "ScarabBeyond",
            ["torment"] = "ScarabTorment",
            ["legion"] = "ScarabLegion",
            ["blight"] = "ScarabBlight",
            ["ritual"] = "ScarabRitual",
            ["harvest"] = "ScarabHarvest",
            ["strongbox"] = "ScarabStrongbox",
            ["domination"] = "ScarabDomination",
            ["betrayal"] = "ScarabBetrayal",
            ["expedition"] = "ScarabExpedition",
            ["abyss"] = "ScarabAbyss",
            ["bestiary"] = "ScarabBeasts",
            ["essence"] = "ScarabEssence",
            ["incursion"] = "ScarabIncursion",
            ["ultimatum"] = "ScarabUltimatum",
            ["ambush"] = "ScarabStrongbox",
            ["harbinger"] = "ScarabHarbinger",
            ["cartography"] = "ScarabMaps",
            ["titanic"] = "ScarabTitanic",
            ["kalguuran"] = "ScarabSettlers",
        };

        foreach (var (statId, name) in statIdToName)
        {
            if (name.StartsWith("chance_%_to_drop_additional_", StringComparison.Ordinal))
            {
                string suffix = name["chance_%_to_drop_additional_".Length..];
                if (suffix.StartsWith("divination_cards", StringComparison.Ordinal))
                    divMinion.Add(statId);
                else if (knownCurrencySuffixes.TryGetValue(suffix, out string? itemId))
                    currencyMap[statId] = itemId;
                else if (suffix.StartsWith("scarabs_", StringComparison.Ordinal)
                    && scarabCategoryPrefixes.TryGetValue(suffix["scarabs_".Length..], out string? scPref))
                    scarabMap[statId] = scPref;
            }
            else if (name.StartsWith("map_boss_additional_", StringComparison.Ordinal)
                && name.EndsWith("_to_drop", StringComparison.Ordinal))
            {
                string middle = name["map_boss_additional_".Length..^"_to_drop".Length];
                if (middle.StartsWith("divination_cards", StringComparison.Ordinal))
                    divBoss.Add(statId);
                else if (knownCurrencySuffixes.TryGetValue(middle, out string? itemId))
                    currencyMap[statId] = itemId;
            }
            else if (name.StartsWith("monster_drops_X_additional_scarabs_", StringComparison.Ordinal))
            {
                string suffix = name["monster_drops_X_additional_scarabs_".Length..];
                if (scarabCategoryPrefixes.TryGetValue(suffix, out string? scPref))
                    scarabMap[statId] = scPref;
            }
            else if (name.StartsWith("monster_chance_%_to_drop_additional_scarabs_", StringComparison.Ordinal))
            {
                string suffix = name["monster_chance_%_to_drop_additional_scarabs_".Length..];
                if (scarabCategoryPrefixes.TryGetValue(suffix, out string? scPref))
                    scarabMap[statId] = scPref;
            }
        }

        var allScarabIds = new List<string>();
        if (ClientTables.Has("BaseItemTypes"))
        {
            foreach (var r in ClientTables.Table("BaseItemTypes"))
            {
                string id = r.Text("Id") ?? "";
                if (id.StartsWith("Metadata/Items/Scarabs/", StringComparison.OrdinalIgnoreCase))
                    allScarabIds.Add(id);
            }
        }

        var scarabsByStat = new Dictionary<uint, string[]>();
        foreach (var (statId, prefix) in scarabMap)
        {
            string fullPrefix = "Metadata/Items/Scarabs/" + prefix;
            string[] matching = [.. allScarabIds.Where(id => id.StartsWith(fullPrefix, StringComparison.OrdinalIgnoreCase))];
            if (matching.Length > 0) scarabsByStat[statId] = matching;
        }

        _currencyDropStatToItem = currencyMap;
        _scarabDropStatToItems = scarabsByStat;
        _divCardMinionDropStats = divMinion;
        _divCardBossDropStats = divBoss;
    }

    // ---- BaseData Map Serialization (12 Exact Verified Hashes, elemType = 8) ----

    /// <summary>Builds the <c>0x02bb</c> <c>BaseData</c> entries for an Eldritch Altar using the 12 binary-verified
    /// <c>u32 LE</c> key hashes (<c>act11-protocol-spec.md §2.3</c>).</summary>
    public static IReadOnlyList<PrimordialAltarObject.BaseDataEntry> BuildBaseDataEntries(AltarOption choice1, AltarOption choice2)
    {
        var entries = new List<PrimordialAltarObject.BaseDataEntry>(12);
        AddOption(entries, choice1,
            [PrimordialAltarObject.Choice1DownsidePrimary, PrimordialAltarObject.Choice1DownsideExtra1, PrimordialAltarObject.Choice1DownsideExtra2],
            [PrimordialAltarObject.Choice1UpsidePrimary, PrimordialAltarObject.Choice1UpsideExtra1, PrimordialAltarObject.Choice1UpsideExtra2]);
        AddOption(entries, choice2,
            [PrimordialAltarObject.Choice2DownsidePrimary, PrimordialAltarObject.Choice2DownsideExtra1, PrimordialAltarObject.Choice2DownsideExtra2],
            [PrimordialAltarObject.Choice2UpsidePrimary, PrimordialAltarObject.Choice2UpsideExtra1, PrimordialAltarObject.Choice2UpsideExtra2]);
        return entries;
    }

    static void AddOption(List<PrimordialAltarObject.BaseDataEntry> into, AltarOption opt,
        ReadOnlySpan<uint> downsideKeys, ReadOnlySpan<uint> upsideKeys)
    {
        for (int i = 0; i < Math.Min(opt.Downsides.Count, downsideKeys.Length); i++)
            into.Add(new(downsideKeys[i], opt.Downsides[i].Def.Hash32, opt.Downsides[i].StatValues));
        for (int i = 0; i < Math.Min(opt.Upsides.Count, upsideKeys.Length); i++)
            into.Add(new(upsideKeys[i], opt.Upsides[i].Def.Hash32, opt.Upsides[i].StatValues));
    }

    // ---- Altar Choice Rolling (with Atlas Passives) ----

    /// <summary>Rolls two Eldritch Altar options (Choice 1 = Top, Choice 2 = Bottom) honoring Atlas passives:
    /// <c>Eldritch Gaze</c> (extra downside + increased upside effect), <c>Wrath of the Cosmos</c> (increased
    /// Searing Exarch downside effect + chance for extra upside), and extra upside/downside altar stats.</summary>
    public static (AltarOption Choice1, AltarOption Choice2)? RollAltarChoices(
        PrimordialBoss boss, bool hardMode, bool allowBossTarget, IReadOnlyDictionary<uint, int>? areaStats, Random rng)
    {
        int[] targetCandidates = allowBossTarget
            ? [TargetPlayer, TargetInfluencedMonsters, TargetMapBoss]
            : [TargetPlayer, TargetInfluencedMonsters];

        bool hasEldritchGaze = (areaStats?.GetValueOrDefault(Stat.AtlasKeystoneEldritchGaze) ?? 0) > 0;
        bool applyTangleAltarScaling = boss == PrimordialBoss.Tangle || !hasEldritchGaze;
        int upsideEffectPct = applyTangleAltarScaling
            ? areaStats?.GetValueOrDefault(Stat.MapPrimordialAltarUpsideEffectPlusPct) ?? 0
            : 0;
        int extraDownsides = applyTangleAltarScaling
            ? Math.Max(0, areaStats?.GetValueOrDefault(Stat.MapPrimordialAltarExtraDownside) ?? 0)
            : 0;
        int exarchDownsideEffectPct = boss == PrimordialBoss.CleansingFire
            ? areaStats?.GetValueOrDefault(Stat.MapSearingExarchAltarDownsideEffectPlusPct) ?? 0
            : 0;
        int exarchExtraUpsideChance = boss == PrimordialBoss.CleansingFire
            ? areaStats?.GetValueOrDefault(Stat.Of("map_searing_exarch_alter_extra_upside")) ?? 0
            : 0;
        int extraOtherUpsideSameDownsideChance =
            areaStats?.GetValueOrDefault(Stat.Of("map_primordial_alter_extra_upside_of_other_type_and_downside_of_same_type_chance")) ?? 0;

        AltarOption? RollOne(HashSet<int> usedModRows)
        {
            int target = targetCandidates[rng.Next(targetCandidates.Length)];
            var downsidePool = GetPool(boss, target, isUpside: false, hardMode);
            var upsidePool = GetPool(boss, target, isUpside: true, hardMode);
            if (downsidePool.Count == 0 || upsidePool.Count == 0)
            {
                // Try any available target type.
                foreach (int alt in targetCandidates)
                {
                    downsidePool = GetPool(boss, alt, isUpside: false, hardMode);
                    upsidePool = GetPool(boss, alt, isUpside: true, hardMode);
                    if (downsidePool.Count > 0 && upsidePool.Count > 0) { target = alt; break; }
                }
            }
            if (downsidePool.Count == 0 || upsidePool.Count == 0) return null;

            var downsides = new List<RolledMod>(3);
            var upsides = new List<RolledMod>(3);

            var primaryDown = PickChoice(downsidePool, usedModRows, rng);
            var primaryUp = PickChoice(upsidePool, usedModRows, rng);
            if (primaryDown is null || primaryUp is null) return null;

            usedModRows.Add(primaryDown.ModRow);
            usedModRows.Add(primaryUp.ModRow);
            downsides.Add(RollValues(primaryDown, exarchDownsideEffectPct, rng));
            upsides.Add(RollValues(primaryUp, upsideEffectPct, rng));

            // Eldritch Gaze / map_primordial_altar_extra_downside: add extra downside(s) of the same target type.
            for (int e = 0; e < Math.Min(extraDownsides, 2); e++)
            {
                if (PickChoice(downsidePool, usedModRows, rng) is { } extraDown)
                {
                    usedModRows.Add(extraDown.ModRow);
                    downsides.Add(RollValues(extraDown, exarchDownsideEffectPct, rng));
                }
            }

            // Wrath of the Cosmos: chance for an extra upside on Searing Exarch altars.
            if (exarchExtraUpsideChance > 0 && upsides.Count < 3 && rng.Next(100) < exarchExtraUpsideChance)
            {
                if (PickChoice(upsidePool, usedModRows, rng) is { } extraUp)
                {
                    usedModRows.Add(extraUp.ModRow);
                    upsides.Add(RollValues(extraUp, upsideEffectPct, rng));
                }
            }

            // Extra upside of other type + extra downside of same type.
            if (extraOtherUpsideSameDownsideChance > 0 && rng.Next(100) < extraOtherUpsideSameDownsideChance)
            {
                if (downsides.Count < 3 && PickChoice(downsidePool, usedModRows, rng) is { } bonusDown)
                {
                    usedModRows.Add(bonusDown.ModRow);
                    downsides.Add(RollValues(bonusDown, exarchDownsideEffectPct, rng));
                }
                if (upsides.Count < 3)
                {
                    int otherTarget = targetCandidates.FirstOrDefault(t => t != target, target);
                    var otherPool = GetPool(boss, otherTarget, isUpside: true, hardMode);
                    if (PickChoice(otherPool, usedModRows, rng) is { } bonusUp)
                    {
                        usedModRows.Add(bonusUp.ModRow);
                        upsides.Add(RollValues(bonusUp, upsideEffectPct, rng));
                    }
                }
            }

            return new AltarOption(target, downsides, upsides);
        }

        var used = new HashSet<int>();
        var c1 = RollOne(used);
        var c2 = RollOne(used);
        return c1 is not null && c2 is not null ? (c1, c2) : null;
    }

    static List<AltarModDef> GetPool(PrimordialBoss boss, int targetType, bool isUpside, bool hardMode)
    {
        if (_pools.TryGetValue((boss, targetType, isUpside, hardMode), out var list) && list.Count > 0)
            return list;
        if (hardMode && _pools.TryGetValue((boss, targetType, isUpside, false), out var normal) && normal.Count > 0)
            return normal;
        return [];
    }

    static AltarModDef? PickChoice(IReadOnlyList<AltarModDef> pool, HashSet<int> exclude, Random rng)
    {
        int totalWeight = 0;
        for (int i = 0; i < pool.Count; i++)
            if (!exclude.Contains(pool[i].ModRow)) totalWeight += pool[i].Weight;
        if (totalWeight <= 0)
        {
            // Fallback to full pool if every entry was already used.
            for (int i = 0; i < pool.Count; i++) totalWeight += pool[i].Weight;
            if (totalWeight <= 0) return null;
            int r2 = rng.Next(totalWeight);
            for (int i = 0; i < pool.Count; i++)
                if ((r2 -= pool[i].Weight) < 0) return pool[i];
            return pool[^1];
        }
        int roll = rng.Next(totalWeight);
        for (int i = 0; i < pool.Count; i++)
        {
            if (exclude.Contains(pool[i].ModRow)) continue;
            if ((roll -= pool[i].Weight) < 0) return pool[i];
        }
        return pool.Last(p => !exclude.Contains(p.ModRow));
    }

    static RolledMod RollValues(AltarModDef def, int effectPlusPct, Random rng)
    {
        int[] values = new int[def.Stats.Count];
        for (int i = 0; i < def.Stats.Count; i++)
        {
            var (_, min, max) = def.Stats[i];
            int v = min == max ? min : rng.Next(min, max + 1);
            if (effectPlusPct != 0 && !(min == 1 && max == 1))
                v = (int)Math.Clamp((long)v * (100L + effectPlusPct) / 100L, int.MinValue, int.MaxValue);
            values[i] = v;
        }
        return new RolledMod(def, values);
    }

    // ---- Altar Spawning on Eldritch Pack Kills ----

    public static async Task OnMonsterKilled(Player player, MonsterInfo monster)
    {
        // Always check Primordial Pinnacle / Mini-boss kills (Infinite Hunger, Black Star, Eater, Exarch).
        var peers = InstanceClients(player);
        await EldritchInfluence.OnPrimordialBossKilled(player, monster.VarietyId, peers);

        var ctx = EldritchInfluence.ContextOf(player);
        if (ctx is null) return;
        if (!EldritchInfluence.IsEldritchMonster(ctx, monster.ObjectId, monster.VarietyId)) return;

        // Advance story non-boss step on Eldritch monster kill (once per instance).
        await EldritchInfluence.OnEldritchMonsterKilled(player, ctx, monster);

        // Decrement remaining count in this monster's Eldritch pack and roll Altar spawn on pack completion
        // (or periodic kill threshold if monster was not tracked by pack index).
        bool rollAltar = false;
        if (monster.ObjectId != 0 && ctx.EldritchMonsters.TryRemove(monster.ObjectId, out int packIndex))
        {
            int left = ctx.PackRemaining.AddOrUpdate(packIndex, 0, (_, prev) => Math.Max(0, prev - 1));
            if (left == 0) rollAltar = true;
        }
        int kills = Interlocked.Increment(ref ctx.EldritchKills);
        if (!rollAltar && kills % 12 == 0) rollAltar = true;
        if (!rollAltar) return;

        await TrySpawnAltarAt(player, ctx, (monster.X, monster.Y), peers);
    }

    /// <summary>Attempts to roll and spawn an Eldritch Altar at <paramref name="at"/> in the current map instance.</summary>
    public static async Task<SpawnedAltar?> TrySpawnAltarAt(Player player, EldritchInfluence.InstanceContext ctx,
        (int X, int Y) at, IReadOnlyList<Player>? peers = null, bool guaranteed = false, Random? rng = null)
    {
        rng ??= Random.Shared;
        var areaStats = player.Instance.AreaStats(() => global::Game.AreaLeagueStats(player));

        lock (ctx.Gate)
        {
            if (ctx.AltarsSpawned >= MaxAltarsPerMap) return null;
        }

        if (!guaranteed)
        {
            int bonusChancePct = ctx.Boss == PrimordialBoss.Tangle
                ? areaStats.GetValueOrDefault(Stat.MapTangleAltarSpawnChancePlusPct)
                : areaStats.GetValueOrDefault(Stat.MapCleansingFireInfluenceAltarSpawnOnInfluenceTickChancePct);
            int chance = (int)Math.Clamp(BasePackAltarSpawnChancePct * (100L + bonusChancePct) / 100L, 5, 95);
            if (rng.Next(100) >= chance) return null;
        }

        var rolled = RollAltarChoices(ctx.Boss, ctx.HardMode, allowBossTarget: !ctx.MapBossDead, areaStats, rng);
        if (rolled is null) return null;

        var cell = player.Layout.Near(at);
        string path = EldritchInfluence.Bosses.TryGetValue(ctx.Boss, out var cfg)
            ? cfg.AltarMetadataPath
            : ctx.Boss == PrimordialBoss.Tangle ? PrimordialAltarObject.TanglePath : PrimordialAltarObject.CleansingFirePath;

        uint entityId = player.TakeObjectId();
        var altar = new SpawnedAltar
        {
            EntityId = entityId,
            Boss = ctx.Boss,
            MetadataPath = path,
            X = cell.X,
            Y = cell.Y,
            Choice1 = rolled.Value.Choice1,
            Choice2 = rolled.Value.Choice2,
        };

        lock (ctx.Gate)
        {
            if (ctx.AltarsSpawned >= MaxAltarsPerMap) return null;
            ctx.Altars[entityId] = altar;
            ctx.AltarsSpawned++;
        }

        byte[] createPacket = InstanceEntities.CreateObject(
            altar.EntityId,
            InstanceEntities.ObjectTypeHash(altar.MetadataPath),
            altar.ToWireObject().Blob());

        foreach (var peer in peers ?? InstanceClients(player))
        {
            if (peer.Closed || peer.LeavingArea || !peer.Connection.PlayerSent) continue;
            try
            {
                await global::Game.SendWorldCreate(peer, altar.EntityId, createPacket,
                    $"0x02bb spawn {ctx.Boss} Eldritch Altar 0x{altar.EntityId:x} at ({altar.X}, {altar.Y})");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        return altar;
    }

    /// <summary>Replays already-spawned Eldritch Altars to a player re-entering or joining the map instance.</summary>
    public static async Task ReplayAltarsToPlayer(Player player, EldritchInfluence.InstanceContext ctx)
    {
        List<SpawnedAltar> snapshot;
        lock (ctx.Gate) snapshot = [.. ctx.Altars.Values];
        if (snapshot.Count == 0) return;
        foreach (var altar in snapshot)
        {
            byte[] createPacket = InstanceEntities.CreateObject(
                altar.EntityId,
                InstanceEntities.ObjectTypeHash(altar.MetadataPath),
                altar.ToWireObject().Blob());
            await global::Game.SendWorldCreate(player, altar.EntityId, createPacket,
                $"0x02bb replay {ctx.Boss} Eldritch Altar 0x{altar.EntityId:x} (activated={altar.Activated})");
        }
    }

    // ---- Handling C→S 0x028e (SelectEldritchAltar) ----

    /// <summary>Handles <c>C→S 0x028e</c> (<c>u32 BE altarEntityId, u8 choice 1|2</c>): validates the altar,
    /// transitions <c>StateMachine.activated = 1</c>, applies the chosen upside &amp; downside modifiers to the
    /// live instance, and broadcasts <c>S→C 0x02c9</c> Ruleset 99 sub-id 0.</summary>
    public static async Task<bool> HandleSelectAltar(Player player, InstanceMapDevice.SelectEldritchAltar req)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (req.Choice is not (1 or 2)) return true;

        var ctx = EldritchInfluence.ContextOf(player);
        if (ctx is null) return true;

        AltarOption chosen;
        SpawnedAltar altar;
        byte[] hudPacket;
        bool playerStatsChanged = false;
        bool minionCombatChanged = false;
        bool bossCombatChanged = false;
        Dictionary<uint, int> minionCombatSnapshot = [];
        Dictionary<uint, int> bossCombatSnapshot = [];

        lock (ctx.Gate)
        {
            if (!ctx.Altars.TryGetValue(req.AltarEntityId, out altar!) || altar.Activated)
                return true;
            altar.Activated = true;
            ctx.AltarsActivated++;
            chosen = req.Choice == 1 ? altar.Choice1 : altar.Choice2;

            foreach (var down in chosen.Downsides)
            {
                switch (down.Def.TargetType)
                {
                    case TargetPlayer:
                        ctx.PlayerDownsides.Add(down);
                        AccumulateStats(ctx.ActivePlayerStats, down);
                        playerStatsChanged = true;
                        break;
                    case TargetInfluencedMonsters:
                        ctx.MinionDownsides.Add(down);
                        AccumulateStats(ctx.ActiveMinionCombatStats, down);
                        minionCombatChanged = true;
                        break;
                    case TargetMapBoss:
                        ctx.BossDownsides.Add(down);
                        AccumulateStats(ctx.ActiveBossCombatStats, down);
                        bossCombatChanged = true;
                        break;
                }
            }

            foreach (var up in chosen.Upsides)
            {
                switch (up.Def.TargetType)
                {
                    case TargetPlayer:
                        ctx.PlayerUpsides.Add(up);
                        AccumulateStats(ctx.ActivePlayerDropStats, up);
                        break;
                    case TargetInfluencedMonsters:
                        ctx.MinionUpsides.Add(up);
                        AccumulateStats(ctx.ActiveMinionDropStats, up);
                        break;
                    case TargetMapBoss:
                        ctx.BossUpsides.Add(up);
                        AccumulateStats(ctx.ActiveBossDropStats, up);
                        break;
                }
            }

            hudPacket = BuildHudPacketLocked(ctx);
            if (ctx.ActivePlayerDropStats.Count > 0)
                ctx.PlayerDropSnapshot = new Dictionary<uint, int>(ctx.ActivePlayerDropStats);
            if (ctx.ActiveMinionDropStats.Count > 0)
                ctx.MinionDropSnapshot = new Dictionary<uint, int>(ctx.ActiveMinionDropStats);
            if (ctx.ActiveBossDropStats.Count > 0)
                ctx.BossDropSnapshot = new Dictionary<uint, int>(ctx.ActiveBossDropStats);
            if (minionCombatChanged) minionCombatSnapshot = new Dictionary<uint, int>(ctx.ActiveMinionCombatStats);
            if (bossCombatChanged) bossCombatSnapshot = new Dictionary<uint, int>(ctx.ActiveBossCombatStats);
        }

        // Apply combat downsides immediately to living/dormant Eldritch monsters and Map Bosses in this instance.
        if (minionCombatChanged)
            ApplyCombatStatsToMonsters(player, ctx, minionCombatSnapshot, isBoss: false);
        if (bossCombatChanged)
            ApplyCombatStatsToMonsters(player, ctx, bossCombatSnapshot, isBoss: true);

        byte[] activatePacket = PrimordialAltarObject.ActivatedUpdate(altar.EntityId, doodads: true);
        var peers = InstanceClients(player);
        foreach (var peer in peers)
        {
            if (peer.Closed || peer.LeavingArea || !peer.Connection.PlayerSent) continue;
            try
            {
                await global::Game.SendWorldRecord(peer, altar.EntityId, activatePacket,
                    $"0x02bc Eldritch Altar 0x{altar.EntityId:x} activated (choice {req.Choice})");
                await Send(peer, hudPacket,
                    $"0x02c9 AtlasPrimordialBossInfluenceRules sub-id 0 (altar #{ctx.AltarsActivated})");
                if (playerStatsChanged)
                {
                    global::Game.RecomputePoolsAndSpeed(peer);
                    await global::Game.SendCharacterStats(peer, PlayerStatChange.Build);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        Console.WriteLine($"[atlas] {player.Connection.Remote} <= 0x028e Eldritch Altar 0x{altar.EntityId:x} choice {req.Choice} " +
            $"({chosen.Downsides.Count} downside(s), {chosen.Upsides.Count} upside(s), total altars={ctx.AltarsActivated})");
        return true;
    }

    static void AccumulateStats(Dictionary<uint, int> into, RolledMod mod)
    {
        for (int i = 0; i < Math.Min(mod.Def.Stats.Count, mod.StatValues.Length); i++)
        {
            uint statId = mod.Def.Stats[i].StatId;
            int value = mod.StatValues[i];
            if (value != 0)
                into[statId] = (int)Math.Clamp((long)into.GetValueOrDefault(statId) + value, int.MinValue, int.MaxValue);
        }
    }

    static void ApplyCombatStatsToMonsters(Player player, EldritchInfluence.InstanceContext ctx,
        IReadOnlyDictionary<uint, int> stats, bool isBoss)
    {
        if (player.Area is null || stats.Count == 0) return;
        var node = AtlasProgress.OfArea(player.Layout.Id);
        foreach (var kv in player.Area.Monsters.Concat(player.Area.DormantMonsters))
        {
            var m = kv.Value;
            if (!m.Hostile || m.Dead || m.Life <= 0) continue;
            bool match = isBoss
                ? (m.Rarity == MonsterRarity.Unique && (node is null || node.Bosses.Contains(m.Variety?.Id ?? "")))
                : EldritchInfluence.IsEldritchMonster(ctx, kv.Key, m.Variety?.Id ?? "");
            if (match)
                m.SetTemporaryStats("eldritch.altar", stats);
        }
    }

    // ---- S→C 0x02c9 Ruleset 99 HUD Helper ----

    /// <summary>Builds the current <c>S→C 0x02c9</c> Ruleset 99 sub-id 0 payload for the player's instance
    /// (empty lists when no altar has been activated yet).</summary>
    public static byte[] CurrentHudMessage(Player player)
    {
        var ctx = EldritchInfluence.ContextOf(player);
        if (ctx is null)
            return InstanceRulesets.PrimordialAltarHud([], [], [], [], [], []);
        lock (ctx.Gate)
            return BuildHudPacketLocked(ctx);
    }

    static byte[] BuildHudPacketLocked(EldritchInfluence.InstanceContext ctx) =>
        InstanceRulesets.PrimordialAltarHud(
            [.. ctx.PlayerUpsides.Select(m => m.ToHudMod())],
            [.. ctx.PlayerDownsides.Select(m => m.ToHudMod())],
            [.. ctx.MinionUpsides.Select(m => m.ToHudMod())],
            [.. ctx.MinionDownsides.Select(m => m.ToHudMod())],
            [.. ctx.BossUpsides.Select(m => m.ToHudMod())],
            [.. ctx.BossDownsides.Select(m => m.ToHudMod())]);

    // ---- Live Gameplay Hooks (Player Stats, Incoming Damage, Drops) ----

    /// <summary>Applies active Player Eldritch Altar downside stats to the character's stat sheet in that map.</summary>
    public static void ApplyPlayerStats(StatSheet sheet)
    {
        var ctx = EldritchInfluence.ContextOf(sheet.Player);
        if (ctx is null) return;
        lock (ctx.Gate)
        {
            if (ctx.ActivePlayerStats.Count == 0) return;
            foreach (var (statId, value) in ctx.ActivePlayerStats)
                if (value != 0) sheet.Add(statId, value);
        }
    }

    /// <summary>Applies per-altar increased damage taken (<c>map_cleansing_fire_influenced_map_player_damage_taken_+%_per_altar_used</c>).</summary>
    public static int AdjustIncomingDamage(Player player, int damage)
    {
        if (damage <= 0) return damage;
        var ctx = EldritchInfluence.ContextOf(player);
        if (ctx is null || ctx.AltarsActivated == 0) return damage;
        int perAltar = global::Game.MapModsOf(player)?.Get(Stat.MapCleansingFireInfluencedMapPlayerDamageTakenPlusPctPerAltarUsed) ?? 0;
        if (perAltar <= 0) return damage;
        long scaled = (long)damage * (100L + (long)perAltar * ctx.AltarsActivated) / 100L;
        return (int)Math.Clamp(scaled, 1, int.MaxValue);
    }

    /// <summary>Applies Player, Eldritch Minion, and Map Boss Eldritch Altar drop upsides and duplication chances.</summary>
    public static void ApplyDrops(DropRoll roll)
    {
        var ctx = EldritchInfluence.ContextOf(roll.Player);
        if (ctx is null) return;
        int altarsUsed = Volatile.Read(ref ctx.AltarsActivated);
        if (altarsUsed == 0) return;

        var playerDropStats = ctx.PlayerDropSnapshot;
        var minionDropStats = ctx.MinionDropSnapshot;
        var bossDropStats = ctx.BossDropSnapshot;
        if (playerDropStats is null && minionDropStats is null && bossDropStats is null) return;

        // 1. Player upsides: map IIQ/IIR and category duplication chances apply to every kill in the map.
        if (playerDropStats is { Count: > 0 })
        {
            int iiq = playerDropStats.GetValueOrDefault(Stat.MapItemDropQuantityPlusPct);
            int iir = playerDropStats.GetValueOrDefault(Stat.MapItemDropRarityPlusPct);
            if (iiq != 0) roll.Quantity *= Math.Max(0.1, 1.0 + iiq / 100.0);
            if (iir != 0) roll.Rarity *= Math.Max(0.1, 1.0 + iir / 100.0);

            int dupCurrency = playerDropStats.GetValueOrDefault(Stat.PctChanceToDuplicateDroppedCurrency);
            int dupGems = playerDropStats.GetValueOrDefault(Stat.PctChanceToDuplicateDroppedGems);
            int dupUniques = playerDropStats.GetValueOrDefault(Stat.PctChanceToDuplicateDroppedUniques);
            int dupScarabs = playerDropStats.GetValueOrDefault(Stat.PctChanceToDuplicateDroppedScarabs);
            int dupMaps = playerDropStats.GetValueOrDefault(Stat.PctChanceToDuplicateDroppedMaps);
            int dupCards = playerDropStats.GetValueOrDefault(Stat.PctChanceToDuplicateDroppedDivinationCards);

            if (dupCurrency > 0 || dupGems > 0 || dupUniques > 0 || dupScarabs > 0 || dupMaps > 0 || dupCards > 0)
            {
                roll.Changes.Add((drop, rng) =>
                {
                    int chance = drop.Type.Class switch
                    {
                        "StackableCurrency" when drop.Type.Id.StartsWith("Metadata/Items/Currency/", StringComparison.Ordinal) => dupCurrency,
                        "Active Skill Gem" or "Support Skill Gem" => dupGems,
                        "MapFragment" when drop.Type.Id.StartsWith("Metadata/Items/Scarabs/", StringComparison.Ordinal) => dupScarabs,
                        "Map" or "MapKey" => dupMaps,
                        "DivinationCard" => dupCards,
                        _ when drop.Item.Rarity == ItemRarity.Unique => dupUniques,
                        _ => 0,
                    };
                    if (chance > 0 && rng.Next(100) < chance)
                        return [drop, drop];
                    return [drop];
                });
            }
        }

        if (minionDropStats is null && bossDropStats is null) return;

        var mapMods = global::Game.MapModsOf(roll.Player);
        int dupEldritchCurrency = (ctx.Boss == PrimordialBoss.Tangle
            ? mapMods?.Get(Stat.MapTangleInfluencedMapDuplicatePrimordialCurrencyChancePct) ?? 0
            : (mapMods?.Get(Stat.MapCleansingFireInfluencedMapDuplicatePrimordialCurrencyChancePct) ?? 0)
              + (mapMods?.Get(Stat.MapCleansingFireInfluencedMapDuplicatePrimordialCurrencyChancePctPerAltarUsed) ?? 0) * altarsUsed);

        var rng = Random.Shared;
        var catalog = ItemsFor(roll.Player);
        var drops = DropsFor(roll.Player);
        var league = LeagueFor(roll.Player);

        // 2. Eldritch Minion upsides: percentage chance per Eldritch minion kill.
        if (minionDropStats is { Count: > 0 } && EldritchInfluence.IsEldritchMonster(ctx, roll.Monster.ObjectId, roll.Monster.VarietyId))
        {
            foreach (var (statId, pct) in minionDropStats)
            {
                if (pct <= 0) continue;
                int count = pct / 100 + (rng.Next(100) < pct % 100 ? 1 : 0);
                if (count <= 0) continue;
                AddAltarDropReward(roll, statId, count, dupEldritchCurrency, catalog, drops, league, rng);
            }
        }

        // 3. Map Boss upsides: guaranteed count per Map Boss kill.
        bool isMapBoss = bossDropStats is { Count: > 0 }
            && roll.Monster.Rarity == MonsterRarity.Unique
            && (AtlasProgress.OfArea(roll.Player.Layout.Id)?.Bosses.Contains(roll.Monster.VarietyId) ?? false);
        if (isMapBoss)
        {
            foreach (var (statId, count) in bossDropStats!)
            {
                if (count <= 0) continue;
                AddAltarDropReward(roll, statId, count, dupEldritchCurrency, catalog, drops, league, rng);
            }
        }
    }

    static void AddAltarDropReward(DropRoll roll, uint statId, int count, int dupEldritchCurrency,
        ItemCatalog catalog, ItemDrops drops, LeagueRuntime league, Random rng)
    {
        if (_currencyDropStatToItem.TryGetValue(statId, out string? itemId))
        {
            int total = count;
            if (dupEldritchCurrency > 0 && itemId.Contains("Eldritch", StringComparison.OrdinalIgnoreCase))
                for (int i = 0; i < count; i++)
                    if (rng.Next(100) < dupEldritchCurrency) total++;
            if (catalog.ById(itemId) is { Supported: true })
                roll.Stacks.Add((itemId, total));
            return;
        }

        if (_scarabDropStatToItems.TryGetValue(statId, out string[]? matching) && matching.Length > 0)
        {
            for (int i = 0; i < count; i++)
            {
                string picked = matching[rng.Next(matching.Length)];
                if (catalog.ById(picked) is { Supported: true })
                    roll.Items.Add(picked);
            }
            return;
        }

        if (_divCardMinionDropStats.Contains(statId) || _divCardBossDropStats.Contains(statId))
        {
            var dropCtx = DropContext.MapCurrency(roll.Monster.Rarity, areaId: roll.Player.Layout.Id,
                leagueId: league.Id, hardMode: league.HardMode);
            for (int i = 0; i < count; i++)
                if (drops.Card(rng, roll.Monster.Level, null, dropCtx) is { } card)
                    roll.Made.Add(card.Item);
        }
    }

    static List<Player> InstanceClients(Player player)
    {
        var peers = InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance)
            && c.Connection.PlayerSent && !c.LeavingArea && !c.Closed).ToList();
        return peers.Contains(player) ? peers : [player, .. peers];
    }
}
