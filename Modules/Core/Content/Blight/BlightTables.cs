using GameServer.Core;

namespace GameServer.Modules.Blight;

/// <summary>
/// All 18 Blight client tables plus supporting tables (Chests, Mods, BaseItemTypes, MonsterMapBossDifficulty,
/// NPCTextAudio, QuestFlags, PassiveSkills) built once when the module loads (<see cref="BlightModule.LoadData"/>).
/// </summary>
public static class BlightTables
{
    /// <summary>One BlightTowers row; <see cref="Row"/> is what C→S 0x265 names. Row 0 is the empty foundation.</summary>
    public sealed record Tower(int Row, string Id, string Name, string Tier, int Radius, IReadOnlyList<int> Next,
        int Cost, int Variety, int MinLevel, int Archetype);

    /// <summary>One BlightEncounterTypes row: lane monster theme, generic flag, roll weight and linked boss-only row.</summary>
    public sealed record EncounterType(int Row, string Id, bool IsGeneric, int Weight, int BossTypeRow);

    /// <summary>One BlightBalancePerLevel row (levels 1..83, plus 101=Blighted and 102=Blight-ravaged).</summary>
    public sealed record BalanceRow(int Level, int BudgetPct, IReadOnlyList<int> KillResource, int PhaseSeconds,
        int DifficultyTier, int PumpDurability, int PhaseCount, int PortalCap);

    /// <summary>One BlightTowerAuras row: buff definition, target kind (0=Towers, 1=Players, 2=Enemies) and MiscAnimated row.</summary>
    public sealed record TowerAura(int Row, int BuffRow, int Kind, int MiscAnimated);

    /// <summary>One Blight chest from BlightChestTypes or salvaged tower chests joined with Chests.</summary>
    public sealed record BlightChest(int ChestsRow, string Id, string Name, string InheritsFrom);

    /// <summary>One BlightRewardTypes row.</summary>
    public sealed record RewardType(int Row, string Id);

    /// <summary>One Oil from BlightCraftingItems joined with BaseItemTypes.</summary>
    public sealed record OilItem(int Row, int BaseItemTypesRow, string Id, string Name, int Tier, int UseType, string NameShort);

    /// <summary>One BlightCraftingRecipes row joined with BlightCraftingResults (all 576 amulet/unique, ring, and map recipes).</summary>
    public sealed record CraftingRecipe(int Row, string Id, int CraftingType, int[] OilRows, int? PassiveSkillRow, ushort PassiveGraphId, int? ModRow, ushort ModHash);

    /// <summary>One BlightedSporeAuras row for Breathstealer spores.</summary>
    public sealed record SporeAura(int Row, int BuffRow, IReadOnlyList<int> StatValues, int Radius, bool AffectsEnemies, int Weight);

    public const ushort CassiaNpcRow = 396;
    public const ushort CassiaHideoutNpcRow = 397;
    public const string CassiaWildVariety = "Metadata/Monsters/Masters/BlightBuilderWild";
    public const string CassiaHideoutNpcId = "Metadata/NPC/League/Blight/BlightBuilderHideout";
    public const string BlightChestBaseType = "Metadata/Chests/BlightChest";
    public const string BlightTowerChestBaseType = "Metadata/Chests/BlightTowerChest";

    /// <summary>QuestFlags rows for Blight and "Strange Growths" (Quest 122).</summary>
    public static class QuestFlagIds
    {
        public const string Received = "BlightQuestReceived";             // row 2041
        public const string Complete = "BlightEncounterComplete";         // row 2042
        public const string Failed = "BlightEncounterFailed";             // row 2043
        public const string Started = "BlightEncounterStarted";           // row 2044
        public const string TowerBuilt = "BlightTowerBuilt";              // row 2045
        public const string Success = "BlightEncounterSuccess";           // row 2046
        public const string AnointingUnlocked = "CassiaOnAnointingOils";  // row 2058
        public const string HaveOwnedOil = "HaveOwnedOil";                // row 2059
        public const string EndgameUnlocked = "HaveUnlockedBlightEndgame";// row 2060
        public const string QuestComplete = "BlightQuestComplete";        // row 2061
        public const string Tutorial = "BlightTutorialEncounter";         // row 2062
    }

    /// <summary>Salvaged tower chest Ids (Chests rows 1772..1775) for Atlas Notable Templar Ingenuity (Tier I..IV).</summary>
    public static readonly string[] SalvagedTowerChests =
    [
        "Metadata/Chests/BlightTowerChestRank1",
        "Metadata/Chests/BlightTowerChestRank2",
        "Metadata/Chests/BlightTowerChestRank3",
        "Metadata/Chests/BlightTowerChestRank4",
    ];

    public static IReadOnlyList<Tower> Towers { get; private set; } = [];
    public static IReadOnlyList<int> KillResource { get; private set; } = [];
    public static IReadOnlyList<BalanceRow> BalanceRows { get; private set; } = [];
    public static IReadOnlyList<BlightLayout.Topology> Topologies { get; private set; } = [];
    public static IReadOnlyList<BlightLayout.Node> Nodes { get; private set; } = [];
    public static IReadOnlyList<EncounterType> EncounterTypes { get; private set; } = [];
    public static IReadOnlyList<TowerAura> TowerAuras { get; private set; } = [];
    public static IReadOnlyList<BlightChest> Chests { get; private set; } = [];
    public static IReadOnlyList<RewardType> RewardTypes { get; private set; } = [];
    public static IReadOnlyList<OilItem> Oils { get; private set; } = [];
    public static IReadOnlyDictionary<int, OilItem> OilByBaseRow { get; private set; } = new Dictionary<int, OilItem>();
    public static IReadOnlyDictionary<string, OilItem> OilByItemId { get; private set; } = new Dictionary<string, OilItem>(StringComparer.Ordinal);
    public static IReadOnlyList<CraftingRecipe> Recipes { get; private set; } = [];
    public static IReadOnlySet<string> AnointableUniqueNames { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlySet<uint> AnointableUniqueWordHashes { get; private set; } = new HashSet<uint>();
    public static IReadOnlyList<SporeAura> SporeAuras { get; private set; } = [];
    public static IReadOnlyDictionary<string, ushort[]> CassiaVoices { get; private set; } = new Dictionary<string, ushort[]>();
    public static IReadOnlySet<string> ValidQuestFlags { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Implicit mod hashes for Blighted (`InfectedMap`, 51426) and Blight-ravaged (`UberInfectedMap__`, 2167) maps.</summary>
    public static ushort InfectedMapModHash { get; private set; }
    public static ushort UberInfectedMapModHash { get; private set; }

    /// <summary>Mods HASH16 for `GrantedPassive`..`GrantedPassive5` (rows 21489..21493).</summary>
    public static ushort[] GrantedPassiveModHashes { get; private set; } = [];

    static (int Level, int MoreLife)[] _bossLifeScaling = [];
    static Dictionary<int, int> _mapBossLifePct = [];

    /// <summary>Returns the BlightBalancePerLevel row matching the area level (or virtual level 101 for Blighted, 102 for Blight-ravaged).</summary>
    public static BalanceRow BalanceFor(int level, bool blighted = false, bool uberBlighted = false)
    {
        int target = uberBlighted ? 102 : blighted ? 101 : level;
        if (BalanceRows.Count == 0)
            return new(target, 100, [4, 12, 50, 250], blighted || uberBlighted ? 60 : 20, uberBlighted ? 5 : blighted ? 4 : 2,
                blighted || uberBlighted ? 20 : 10, blighted || uberBlighted ? 5 : 2, blighted || uberBlighted ? 20 : 5);
        var best = BalanceRows[0];
        foreach (var row in BalanceRows)
        {
            if (row.Level <= target) best = row;
            else break;
        }
        return best;
    }

    /// <summary>
    /// Raw BlightBossLifeScalingPerLevel.MoreLife for <paramref name="level"/> (negative for lvl 73..84 because it offsets
    /// MonsterMapBossDifficulty.BossLifePercentIncrease).
    /// </summary>
    public static int BossMoreLifeRawPct(int level)
    {
        if (_bossLifeScaling.Length == 0) return 0;
        int best = _bossLifeScaling[0].MoreLife;
        foreach (var (lvl, more) in _bossLifeScaling)
        {
            if (lvl <= level) best = more;
            else break;
        }
        return best;
    }

    /// <summary>
    /// Proven effective Blight boss more-life percent at <paramref name="level"/>:
    /// <c>MonsterMapBossDifficulty.BossLifePercentIncrease + BlightBossLifeScalingPerLevel.MoreLife</c>,
    /// producing the exact smooth monotonic curve (+256% at lvl 68 up to +552% at lvl 81–85; 0 below lvl 68).
    /// </summary>
    public static int BossEffectiveMoreLifePct(int level)
    {
        if (level < 68) return 0;
        int clamped = Math.Clamp(level, 68, 84);
        int mapBoss = _mapBossLifePct.TryGetValue(clamped, out int mb) ? mb : 180;
        int raw = BossMoreLifeRawPct(clamped);
        return Math.Max(0, mapBoss + raw);
    }

    public static void Load()
    {
        var perLevel = new Dictionary<int, (int Cost, int Variety, int MinLevel)>();
        foreach (var r in ClientTables.TableOrEmpty("BlightTowersPerLevel"))
            perLevel[r.Int("BlightTowersKey")] = (r.Int("Cost"), r.Int("MonsterVarietiesKey"), r.Int("__col1"));
        Towers = [.. ClientTables.TableOrEmpty("BlightTowers").Select(r =>
        {
            var (cost, variety, minLevel) = perLevel.GetValueOrDefault(r.Index, (0, -1, 1));
            return new Tower(r.Index, r.Text("Id") ?? "", r.Text("Name") ?? "", r.Text("Tier") ?? "",
                r.Int("Radius"), r.Ints("NextUpgradeOptions"), cost, variety, Math.Max(1, minLevel), r.Int("__col8"));
        })];

        BalanceRows = [.. ClientTables.TableOrEmpty("BlightBalancePerLevel")
            .Select(r => new BalanceRow(r.Int("Level"), r.Int("__col1"), r.Ints("__col2"), r.Int("__col3"),
                r.Int("__col4"), r.Int("__col5"), r.Int("__col6"), r.Int("__col8")))
            .OrderBy(b => b.Level)];
        KillResource = BalanceRows.Count > 0 ? BalanceRows[0].KillResource : [4, 12, 50, 250];

        var topologies = ClientTables.TableOrEmpty("BlightTopologies");
        Nodes = BlightLayout.ReadNodes(ClientTables.TableOrEmpty("BlightTopologyNodes"), [.. topologies.Select(r => r.Int("BlightTopologyNodesKey"))]);
        Topologies = [.. topologies.Select(r => new BlightLayout.Topology(r.Index, r.Text("Id") ?? "", r.Int("BlightTopologyNodesKey"),
            r.Int("__col2"), r.Int("__col3"), r.Int("__col4"), r.Int("__col6"))).Where(t => t.Root >= 0 && t.Root < Nodes.Count)];

        EncounterTypes = [.. ClientTables.TableOrEmpty("BlightEncounterTypes").Select(r =>
            new EncounterType(r.Index, r.Text("Id") ?? "", r.Int("IsGeneric") != 0, r.Int("Weight"), r.IntOrNull("__col4") ?? -1))];

        _bossLifeScaling = [.. ClientTables.TableOrEmpty("BlightBossLifeScalingPerLevel")
            .Select(r => (r.Int("Level"), r.Int("MoreLife")))
            .OrderBy(x => x.Item1)];

        var mbd = new Dictionary<int, int>();
        foreach (var r in ClientTables.TableOrEmpty("MonsterMapBossDifficulty"))
            mbd[r.Int("MapLevel")] = r.Int("BossLifePercentIncrease");
        _mapBossLifePct = mbd;

        TowerAuras = [.. ClientTables.TableOrEmpty("BlightTowerAuras").Select(r =>
            new TowerAura(r.Index, r.Int("BuffDefinitionsKey"), r.Int("__col2"), r.Int("MiscAnimatedKey")))];

        var chestRows = new Dictionary<int, (string Id, string Name, string InheritsFrom)>();
        foreach (var r in ClientTables.TableOrEmpty("Chests"))
            chestRows[r.Index] = (r.Text("Id") ?? "", r.Text("Name") ?? "", r.Text("InheritsFrom") ?? "");
        Chests = [.. ClientTables.TableOrEmpty("BlightChestTypes")
            .Select(r => r.Int("ChestsKey"))
            .Where(chestRows.ContainsKey)
            .Select(k => new BlightChest(k, chestRows[k].Id, chestRows[k].Name, chestRows[k].InheritsFrom))];

        RewardTypes = [.. ClientTables.TableOrEmpty("BlightRewardTypes").Select(r =>
            new RewardType(r.Index, r.Text("Id") ?? ""))];

        var baseItems = new Dictionary<int, (string Id, string Name)>();
        foreach (var r in ClientTables.TableOrEmpty("BaseItemTypes"))
            baseItems[r.Index] = (r.Text("Id") ?? "", r.Text("Name") ?? "");
        Oils = [.. ClientTables.TableOrEmpty("BlightCraftingItems")
            .Select(r =>
            {
                int oilRow = r.Int("Oil");
                var (id, name) = baseItems.GetValueOrDefault(oilRow, ("", ""));
                return new OilItem(r.Index, oilRow, id, name, r.Int("Tier"), r.Int("UseType"), r.Text("NameShort") ?? "");
            })
            .Where(o => o.Id.Length > 0)];
        OilByBaseRow = Oils.ToDictionary(o => o.BaseItemTypesRow);
        OilByItemId = Oils.ToDictionary(o => o.Id, StringComparer.Ordinal);

        // Mods hashes: InfectedMap, UberInfectedMap__, GrantedPassive..5, and recipe mod lookup.
        ushort infectedHash = 0, uberInfectedHash = 0;
        var grantedHashes = new ushort[5];
        var modHashByRow = new Dictionary<int, ushort>();
        foreach (var r in ClientTables.TableOrEmpty("Mods"))
        {
            if (r.IntOrNull("HASH16") is not int h || h <= 0) continue;
            ushort hash = (ushort)h;
            modHashByRow[r.Index] = hash;
            string modId = r.Text("Id") ?? "";
            switch (modId)
            {
                case "InfectedMap": infectedHash = hash; break;
                case "UberInfectedMap__": uberInfectedHash = hash; break;
                case "GrantedPassive": grantedHashes[0] = hash; break;
                case "GrantedPassive2": grantedHashes[1] = hash; break;
                case "GrantedPassive3": grantedHashes[2] = hash; break;
                case "GrantedPassive4": grantedHashes[3] = hash; break;
                case "GrantedPassive5": grantedHashes[4] = hash; break;
            }
        }
        InfectedMapModHash = infectedHash;
        UberInfectedMapModHash = uberInfectedHash;
        GrantedPassiveModHashes = [.. grantedHashes.Where(h => h != 0)];

        // PassiveSkills GraphId lookup for BlightCraftingResults.PassiveSkill.
        var passiveGraphByRow = new Dictionary<int, ushort>();
        foreach (var r in ClientTables.TableOrEmpty("PassiveSkills"))
            if (r.IntOrNull("PassiveSkillGraphId") is int gid && gid > 0)
                passiveGraphByRow[r.Index] = (ushort)gid;

        // BlightCraftingResults (1-to-1 with BlightCraftingRecipes by _row / BlightCraftingResultsKey).
        var craftResults = new Dictionary<int, (int? ModRow, int? PassiveRow)>();
        foreach (var r in ClientTables.TableOrEmpty("BlightCraftingResults"))
            craftResults[r.Index] = (r.IntOrNull("Mod"), r.IntOrNull("PassiveSkill"));

        var recipes = new List<CraftingRecipe>();
        foreach (var r in ClientTables.TableOrEmpty("BlightCraftingRecipes"))
        {
            int resKey = r.IntOrNull("BlightCraftingResultsKey") ?? r.Index;
            var (modRow, passiveRow) = craftResults.GetValueOrDefault(resKey, (null, null));
            ushort graphId = passiveRow is int pr && passiveGraphByRow.TryGetValue(pr, out ushort g) ? g : (ushort)0;
            ushort modHash = modRow is int mr && modHashByRow.TryGetValue(mr, out ushort mh) ? mh : (ushort)0;
            int[] oils = [.. r.Ints("BlightCraftingItemsKeys")];
            if (oils.Length == 0) continue;
            Array.Sort(oils);
            recipes.Add(new CraftingRecipe(r.Index, r.Text("Id") ?? "", r.Int("BlightCraftingTypesKey"), oils, passiveRow, graphId, modRow, modHash));
        }
        Recipes = recipes;

        // BlightCraftingUniques joined with Words.Text & Words.HASH32 (matching Item.NameWords).
        var wordsByRow = new Dictionary<int, (string Text, uint Hash)>();
        foreach (var r in ClientTables.TableOrEmpty("Words"))
            if (r.Text("Text") is { Length: > 0 } text)
                wordsByRow[r.Index] = (text, r.UInt("HASH32"));
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueWordHashes = new HashSet<uint>();
        foreach (var r in ClientTables.TableOrEmpty("BlightCraftingUniques"))
            if (r.IntOrNull("WordsKey") is int wk && wordsByRow.TryGetValue(wk, out var entry))
            {
                uniqueNames.Add(entry.Text);
                if (entry.Hash != 0) uniqueWordHashes.Add(entry.Hash);
            }
        AnointableUniqueNames = uniqueNames;
        AnointableUniqueWordHashes = uniqueWordHashes;

        SporeAuras = [.. ClientTables.TableOrEmpty("BlightedSporeAuras").Select(r =>
            new SporeAura(r.Index, r.Int("BuffDefinitionsKey"), r.Ints("BuffStatValues"),
                Math.Max(10, r.Int("__col2")), r.Ints("__col3").Contains(3), Math.Max(1, r.Int("__col4"))))];

        var voices = new Dictionary<string, List<ushort>>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("NPCTextAudio"))
        {
            string id = r.Text("Id") ?? "";
            if (!id.StartsWith("BlightBuilder", StringComparison.Ordinal)) continue;
            if (r.Index < 0 || r.Index > ushort.MaxValue) continue;
            if (!voices.TryGetValue(id, out var list)) voices[id] = list = [];
            list.Add((ushort)r.Index);
        }
        CassiaVoices = voices.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);

        var qflags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("QuestFlags"))
            if (r.Text("Id") is { Length: > 0 } qid) qflags.Add(qid);
        ValidQuestFlags = qflags;
    }
}
