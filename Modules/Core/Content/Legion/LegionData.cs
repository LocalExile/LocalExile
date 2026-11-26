using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Legion client.db tables, read once per module load (LegionFactions, LegionRanks, LegionMonsterVarieties,
/// LegionMonsterCounts, LegionChestTypes, LegionChestCounts, LegionBalancePerLevel). Columns whose meaning is not
/// known stay raw ints named by their column number.
/// </summary>
public sealed class LegionData
{
    /// <summary>A LegionFactions row: schema columns plus col3 formation orderliness (0.95 orderly grid, 0.65/0.50 loose).</summary>
    public sealed record Faction(int Row, string Id, int SpawnWeight, int BalanceKey, int StatsKey, int BuffVisualsKey, int[] MiscAnimated, double Orderliness = 0.5);

    /// <summary>A LegionRanks row; col0 is tier (0..3), col1..col4 rarity weights, col7 general/hoard chance, col8 alert value (phase 4).</summary>
    public sealed record Rank(int Row, int Col0, int Col1, int Col2, int Col3, int Col4, int BalanceKey, int Col6, int Col7, int Col8);

    /// <summary>A LegionMonsterVarieties row; col3 meaning unknown, col5 prop chance (%), col6 variety spawn weight,
    /// col10/col11 prop clip time range (ms), col15 prop count per MiscAnimatedKey entry; Mounted is MonsterVarietiesKey2, the form a
    /// general starts in (only Aukuna: 7754 on foot, 7753 mounted), -1 when none.</summary>
    public sealed record MonsterVariety(int Row, int Monster, int Faction, int RankKey, int Col3, int[] MiscAnimated, int PropChance = 0, int SpawnWeight = 1, int[]? PropFromMs = null, int[]? PropToMs = null, int[]? PropCounts = null, int Mounted = -1);

    /// <summary>A general that starts mounted (E45, R17): the mounted form (no drops), the form on foot it becomes at the mount's death
    /// (MapBoss, the loot) and the Invisible bird object the dismount leaves (MonsterVarieties rows).</summary>
    public sealed record MountRows(int Mounted, int OnFoot, int Bird);

    /// <summary>A general whose body and rider are two objects (Hyrri, E46, R17): the targetable body (its LegionMonsterVarieties row), the
    /// untargetable rider that shoots and the water clones its mortar leaves (MonsterVarieties rows).</summary>
    public sealed record RiderRows(int Body, int Rider, int WaterClone);

    /// <summary>A LegionMonsterCounts row; cols 2-7 meaning unknown.</summary>
    public sealed record MonsterCount(int Row, int Faction, int RankKey, int Col2, int Col3, int Col4, int Col5, int Col6, int Col7);

    /// <summary>A LegionChestTypes row; col0 is the frozen chest monster variety, Chest/HardmodeChest are the Chests rows it becomes at release, col3 meaning unknown.</summary>
    public sealed record ChestType(int Row, int Col0, int Chest, int Col3, int Faction, int HardmodeChest = -1);

    /// <summary>A LegionChestCounts row; cols 2-5 and 7 meaning unknown.</summary>
    public sealed record ChestCount(int Row, int Faction, int RankKey, int MinLevel, int Col2, int Col3, int Col4, int Col5, int Col7);

    /// <summary>A LegionBalancePerLevel row from MinLevel; Cols holds col1..col15 (col3 timer ms, col15 Timeless Conflict alert cap).</summary>
    public sealed record Balance(int Row, int MinLevel, int[] Cols)
    {
        /// <summary>col3: timer length in ms (client 0x1416290a0).</summary>
        public int DurationMs => Cols[2];
        /// <summary>col15: maximum Alert level before Schism triggers under the Timeless Conflict keystone (1000).</summary>
        public int AlertMax => Cols.Length >= 15 && Cols[14] > 0 ? Cols[14] : 1000;
    }

    /// <summary>One of the four Domain of Timeless Conflict areas (WorldAreas LegionLeague..LegionLeague4, 2..5 emblems).</summary>
    public sealed record DomainArea(int Row, ushort Hash16, string Id, string Name, int BaseLevel, int[] ModRows);

    /// <summary>A Timeless Emblem (BaseItemTypes 3055..3064) and its MapFragmentMods entry. An Unrelenting one (removed in 3.29.0,
    /// existing items converted to regular emblems) carries its regular emblem's family and mods.</summary>
    public sealed record EmblemInfo(int BaseRow, string BaseId, uint TypeHash, string Name, string FactionId, int Family, int[] ModRows);

    /// <summary>A LegionRewardTypeVisuals row: its minimap icon (MinimapIcons row; the monster stat is the row + 1),
    /// its MiscAnimatedKey (.ao floating symbol above clickable chests) and its scale (__col4). The client draws the symbol by row.</summary>
    public sealed record RewardType(int Row, int MinimapIcon, int MiscAnimatedKey = -1, double Scale = 3.0);

    /// <summary>What a general says: the NPCs row of the speaker (its Id is the general's MonsterVarieties Id) and its NPCTextAudio rows by category
    /// (MapIntro, Defeat, Fleeing, Dismount, BattleCry, Taunt, Injured, Attack; from the row Id's stem, GUESS that the stem is the trigger).</summary>
    public sealed record GeneralVoice(ushort Npc, string NpcId, IReadOnlyDictionary<string, ushort[]> Lines)
    {
        /// <summary>The speaker is Aukuna on foot (her NPCs Id ends in Dismounted).</summary>
        public bool Dismounted => NpcId.EndsWith("Dismounted", StringComparison.Ordinal);
    }

    static readonly string[] VoiceCategories = ["MapIntro", "Defeat", "Fleeing", "Dismount", "BattleCry", "Taunt", "Injured", "Attack", "Arrack"];
    const string VoiceNpcPrefix = "Metadata/Monsters/LegionLeague/";
    readonly Dictionary<string, GeneralVoice> voices = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, GeneralVoice?> voiceByVariety = new(StringComparer.Ordinal);

    /// <summary>The voice of a monster variety by its Id: the speaker whose NPCs Id is the longest prefix of it (the plan's general may be a variant
    /// such as LegionKaruiGeneralFish), or null.</summary>
    public GeneralVoice? VoiceOf(string? varietyId) =>
        varietyId is null ? null : voiceByVariety.GetOrAdd(varietyId, id =>
        {
            GeneralVoice? best = null;
            foreach (var (npcId, voice) in voices)
                if (id.StartsWith(npcId, StringComparison.Ordinal) && (best is null || npcId.Length > best.NpcId.Length)) best = voice;
            return best;
        });

    void LoadVoices()
    {
        var npcRows = new Dictionary<int, string>();
        foreach (var r in ClientTables.TableOrEmpty("NPCs"))
            if (r.Text("Id") is { } id && id.StartsWith(VoiceNpcPrefix, StringComparison.Ordinal) && r.Index <= ushort.MaxValue) npcRows[r.Index] = id;
        var lines = new Dictionary<int, Dictionary<string, List<ushort>>>();
        foreach (var r in ClientTables.TableOrEmpty("NPCTextAudio"))
        {
            if (r.Text("Id") is not { } id || !id.StartsWith("Legion", StringComparison.Ordinal) || r.Index > ushort.MaxValue) continue;
            string? category = VoiceCategories.FirstOrDefault(c => id.Contains(c, StringComparison.Ordinal));
            if (category is null) continue;
            if (category == "Arrack") category = "Attack";
            foreach (int npc in Tolerant(r, "NPCs"))
                if (npcRows.ContainsKey(npc))
                {
                    if (!lines.TryGetValue(npc, out var byCategory)) lines[npc] = byCategory = new(StringComparer.Ordinal);
                    if (!byCategory.TryGetValue(category, out var list)) byCategory[category] = list = [];
                    list.Add((ushort)r.Index);
                }
        }
        foreach (var (npc, byCategory) in lines)
            voices[npcRows[npc]] = new GeneralVoice((ushort)npc, npcRows[npc], byCategory.ToDictionary(p => p.Key, p => p.Value.ToArray()));
    }

    public IReadOnlyList<RewardType> RewardTypes { get; }
    /// <summary>O(1) lookup of LegionRewardTypeVisuals by row index.</summary>
    public IReadOnlyDictionary<int, RewardType> RewardTypesByRow { get; }
    public IReadOnlyList<Faction> Factions { get; }
    public IReadOnlyList<Rank> Ranks { get; }
    public IReadOnlyList<MonsterVariety> MonsterVarieties { get; }
    /// <summary>O(1) lookup of first LegionMonsterVarieties row by MonsterVarietiesKey (for stasis props).</summary>
    public IReadOnlyDictionary<int, MonsterVariety> VarietyByMonster { get; }
    /// <summary>Mounted generals by the MonsterVarieties row of each of their forms (mounted and on foot).</summary>
    public IReadOnlyDictionary<int, MountRows> Mounts { get; }
    /// <summary>Two-object generals by the MonsterVarieties row of the body.</summary>
    public IReadOnlyDictionary<int, RiderRows> Riders { get; }
    /// <summary>The variety a planned monster is spawned as: the mounted form of a general that starts mounted, else the row itself.</summary>
    public int SpawnRow(int monsterRow) => Mounts.TryGetValue(monsterRow, out var m) && m.OnFoot == monsterRow ? m.Mounted : monsterRow;
    public IReadOnlyList<MonsterCount> MonsterCounts { get; }
    public IReadOnlyList<ChestType> ChestTypes { get; }
    public IReadOnlyList<ChestCount> ChestCounts { get; }
    public IReadOnlyList<Balance> Balances { get; }
    /// <summary>Environments rows for LegionLeagueWorld, LegionLeagueWorldBattle, LegionLeagueWorldPostBattle and LegionLeague (Domain).</summary>
    public ushort EnvWorld { get; }
    public ushort EnvBattle { get; }
    public ushort EnvPostBattle { get; }
    /// <summary>Rulesets row for LegionRules, or null when absent from client.db.</summary>
    public byte? RulesetRow { get; }
    /// <summary>MiscAnimated row -> AOFile path, loaded eagerly so hot-reload clears it cleanly.</summary>
    public IReadOnlyDictionary<int, string> MiscAnimatedModels { get; }
    /// <summary>Chests rows referenced by LegionChestTypes (row -> Id and InheritsFrom).</summary>
    public IReadOnlyDictionary<int, (string Id, string? InheritsFrom)> ChestsByRow { get; }
    /// <summary>Chest Id -> InheritsFrom for Legion chests (used when resolving QuestObjectTypes for openable chests).</summary>
    public IReadOnlyDictionary<string, string?> ChestInherits { get; }
    /// <summary>Ordered Domain of Timeless Conflict areas: [0]=2-way (LegionLeague), [1]=3-way, [2]=4-way, [3]=5-way (LegionLeague4).</summary>
    public IReadOnlyList<DomainArea> DomainAreas { get; }
    /// <summary>AtlasNode HASH16 for LegionDomain (50774), or 0 when absent.</summary>
    public ushort DomainAtlasNodeHash { get; }
    /// <summary>Timeless Emblems and Unrelenting Emblems indexed by BaseItemTypes row and TypeHash.</summary>
    public IReadOnlyDictionary<int, EmblemInfo> EmblemsByRow { get; }
    public IReadOnlyDictionary<uint, EmblemInfo> EmblemsByHash { get; }

    static LegionData? Loaded;
    public static LegionData Current => Loaded ?? throw new InvalidOperationException("LegionData not loaded; call LegionModule.LoadData() first.");
    public static LegionData Reload() => Loaded = new LegionData();
    public static void Unload() => Loaded = null;

    /// <summary>An optional column (the prop columns are a guess) that is a list or a number: its numbers, or none when it is neither, so a type surprise
    /// skips the props instead of failing the module.</summary>
    static int[] Tolerant(ClientRow r, string column)
    {
        try { return r.Ints(column); }
        catch (Exception) { }
        try { return r.IntOrNull(column) is int v ? [v] : []; }
        catch (Exception) { return []; }
    }

    LegionData()
    {
        string[] animated = ["MiscAnimatedKey1", "MiscAnimatedKey2", "MiscAnimatedKey3", "MiscAnimatedKey4", "MiscAnimatedKey5"];
        Factions = [.. ClientTables.TableOrEmpty("LegionFactions").Select(r => new Faction(r.Index, r.Text("Id") ?? "", r.Int("SpawnWeight"),
            r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.IntOrNull("StatsKey") ?? -1, r.IntOrNull("BuffVisualsKey") ?? -1,
            [.. animated.Select(c => r.IntOrNull(c) ?? -1)], r.RealOrNull("__col3") ?? 0.5))];
        Ranks = [.. ClientTables.TableOrEmpty("LegionRanks").Select(r => new Rank(r.Index, r.Int("__col0"), r.Int("__col1"), r.Int("__col2"),
            r.Int("__col3"), r.Int("__col4"), r.IntOrNull("LegionBalancePerLevelKey") ?? -1, r.Int("__col6"), r.Int("__col7"), r.Int("__col8")))];
        MonsterVarieties = [.. ClientTables.TableOrEmpty("LegionMonsterVarieties").Select(r =>
        {
            int rank = r.IntOrNull("LegionRanksKey") ?? -1;
            int weight = Tolerant(r, "__col6").FirstOrDefault();
            return new MonsterVariety(r.Index, r.IntOrNull("MonsterVarietiesKey") ?? -1, r.IntOrNull("LegionFactionsKey") ?? -1, rank,
                r.Int("__col3"), r.Ints("MiscAnimatedKey"), Tolerant(r, "__col5").FirstOrDefault(), weight > 0 ? weight : 1,
                Tolerant(r, "__col10"), Tolerant(r, "__col11"), Tolerant(r, "__col15"), r.IntOrNull("MonsterVarietiesKey2") ?? -1);
        })];
        // The generals' companion objects are named after the general (R17: no table links them): the mount's bird is "<mounted>Bird",
        // Hyrri's body "<rider>Fish" and her clones "<rider>Clone".
        var legionRows = new Dictionary<string, int>(StringComparer.Ordinal);
        var legionIds = new Dictionary<int, string>();
        foreach (var r in ClientTables.TableOrEmpty("MonsterVarieties"))
            if (r.Text("Id") is { } vid && vid.StartsWith(VoiceNpcPrefix, StringComparison.Ordinal)) { legionRows.TryAdd(vid, r.Index); legionIds[r.Index] = vid; }
        var mounts = new Dictionary<int, MountRows>();
        var riders = new Dictionary<int, RiderRows>();
        foreach (var mv in MonsterVarieties)
        {
            if (mv.Mounted >= 0 && mv.Monster >= 0 && legionIds.TryGetValue(mv.Mounted, out string? mountedId)
                && legionRows.TryGetValue(mountedId + "Bird", out int bird))
                mounts[mv.Mounted] = mounts[mv.Monster] = new MountRows(mv.Mounted, mv.Monster, bird);
            if (legionIds.TryGetValue(mv.Monster, out string? bodyId) && bodyId.EndsWith("Fish", StringComparison.Ordinal)
                && legionRows.TryGetValue(bodyId[..^4], out int rider) && legionRows.TryGetValue(bodyId[..^4] + "Clone", out int clone))
                riders[mv.Monster] = new RiderRows(mv.Monster, rider, clone);
        }
        Mounts = mounts;
        Riders = riders;
        MonsterCounts = [.. ClientTables.TableOrEmpty("LegionMonsterCounts").Select(r => new MonsterCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("__col2"), r.Int("__col3"), r.Int("__col4"),
            r.Int("__col5"), r.Int("__col6"), r.Int("__col7")))];
        ChestTypes = [.. ClientTables.TableOrEmpty("LegionChestTypes").Select(r => new ChestType(r.Index, r.IntOrNull("__col0") ?? -1,
            r.IntOrNull("Chest") ?? -1, r.Int("__col3"), r.IntOrNull("Faction") ?? -1, r.IntOrNull("HardmodeChest") ?? -1))];
        ChestCounts = [.. ClientTables.TableOrEmpty("LegionChestCounts").Select(r => new ChestCount(r.Index,
            r.IntOrNull("LegionFactionsKey") ?? -1, r.IntOrNull("LegionRanksKey") ?? -1, r.Int("MinLevel"), r.Int("__col2"), r.Int("__col3"),
            r.Int("__col4"), r.Int("__col5"), r.Int("__col7")))];
        RewardTypes = [.. ClientTables.TableOrEmpty("LegionRewardTypeVisuals").Select(r => new RewardType(
            r.Index, r.IntOrNull("MinimapIconsKey") ?? -1, r.IntOrNull("MiscAnimatedKey") ?? -1, r.RealOrNull("__col4") ?? 3.0))];
        RewardTypesByRow = RewardTypes.ToDictionary(r => r.Row);
        var byMonster = new Dictionary<int, MonsterVariety>();
        foreach (var mv in MonsterVarieties)
            if (mv.Monster >= 0) byMonster.TryAdd(mv.Monster, mv);
        VarietyByMonster = byMonster;
        Balances = [.. ClientTables.TableOrEmpty("LegionBalancePerLevel").Select(r => new Balance(r.Index, r.Int("MinLevel"),
            [.. Enumerable.Range(1, 15).Select(c => r.Int($"__col{c}"))]))];
        var environments = ClientTables.TableOrEmpty("Environments");
        ushort Env(string id) => environments.FirstOrDefault(r => r.Text("Id") == id) is { Table: not null } row && row.Index <= ushort.MaxValue ? (ushort)row.Index : (ushort)0;
        EnvWorld = Env("LegionLeagueWorld");
        EnvBattle = Env("LegionLeagueWorldBattle");
        EnvPostBattle = Env("LegionLeagueWorldPostBattle");
        RulesetRow = ClientTables.TableOrEmpty("Rulesets").FirstOrDefault(r => r.Text("Id") == "LegionRules") is { Table: not null } rr && rr.Index <= byte.MaxValue
            ? (byte)rr.Index : null;
        MiscAnimatedModels = ClientTables.TableOrEmpty("MiscAnimated")
            .Where(r => r.Text("AOFile") is { Length: > 0 })
            .ToDictionary(r => r.Index, r => r.Text("AOFile")!);
        var chestRows = new HashSet<int>();
        foreach (var ct in ChestTypes)
        {
            if (ct.Chest >= 0) chestRows.Add(ct.Chest);
            if (ct.HardmodeChest >= 0) chestRows.Add(ct.HardmodeChest);
        }
        var byRow = new Dictionary<int, (string Id, string? InheritsFrom)>();
        var inherits = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in ClientTables.TableOrEmpty("Chests"))
        {
            if (!chestRows.Contains(r.Index) || r.Text("Id") is not { Length: > 0 } cid) continue;
            string? parent = r.Text("InheritsFrom");
            byRow[r.Index] = (cid, parent);
            inherits[cid] = parent;
        }
        ChestsByRow = byRow;
        ChestInherits = inherits;

        // Domain of Timeless Conflict WorldAreas (LegionLeague..LegionLeague4) and AtlasNode (LegionDomain).
        string[] domainIds = ["LegionLeague", "LegionLeague2", "LegionLeague3", "LegionLeague4"];
        var worldAreas = ClientTables.TableOrEmpty("WorldAreas");
        var domains = new List<DomainArea>(4);
        foreach (string areaId in domainIds)
        {
            if (worldAreas.FirstOrDefault(r => r.Text("Id") == areaId) is { Table: not null } wa)
                domains.Add(new DomainArea(wa.Index, (ushort)wa.Int("HASH16"), areaId,
                    wa.Text("Name") ?? "Domain of Timeless Conflict", wa.Int("AreaLevel"), wa.Ints("ModsKeys")));
        }
        DomainAreas = domains;
        DomainAtlasNodeHash = ClientTables.TableOrEmpty("AtlasNode").FirstOrDefault(r => r.Text("Id") == "LegionDomain") is { Table: not null } an
            ? (ushort)an.Int("HASH16") : (ushort)0;

        // Normal & Unrelenting Timeless Emblems from BaseItemTypes + MapFragmentMods.
        var fragModsByBase = new Dictionary<int, (int Family, int[] Mods)>();
        foreach (var fm in ClientTables.TableOrEmpty("MapFragmentMods"))
            if (fm.IntOrNull("BaseItemTypesKey") is int baseRow)
                fragModsByBase[baseRow] = (fm.IntOrNull("MapFragmentFamilies") ?? -1, fm.Ints("ModsKeys"));
        var emblemsByRow = new Dictionary<int, EmblemInfo>();
        var emblemsByHash = new Dictionary<uint, EmblemInfo>();
        const string EmblemPrefix = "Metadata/Items/MapFragments/CurrencyLegionFragment";
        var regularRows = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bt in ClientTables.TableOrEmpty("BaseItemTypes"))
            if (bt.Text("Id") is { } rid && rid.StartsWith(EmblemPrefix, StringComparison.Ordinal) && !rid.EndsWith("Uber", StringComparison.Ordinal))
                regularRows[rid] = bt.Index;
        foreach (var bt in ClientTables.TableOrEmpty("BaseItemTypes"))
        {
            if (bt.Text("Id") is not { } bid || !bid.StartsWith(EmblemPrefix, StringComparison.Ordinal)) continue;
            string suffix = bid[EmblemPrefix.Length..];
            bool uber = suffix.EndsWith("Uber", StringComparison.Ordinal);
            string key = uber ? suffix[..^4] : suffix;
            string? faction = key switch
            {
                "Karui" => "Karui",
                "Maraketh" => "Maraketh",
                "Eternal" => "EternalEmpire",
                "Templar" => "Templar",
                "Vaal" => "Vaal",
                _ => null,
            };
            if (faction is null) continue;
            int modsRow = uber && regularRows.TryGetValue(bid[..^4], out int regular) ? regular : bt.Index;
            var (family, modRows) = fragModsByBase.TryGetValue(modsRow, out var entry) ? entry : (-1, []);
            var info = new EmblemInfo(bt.Index, bid, InstanceEntities.ObjectTypeHash(bid), bt.Text("Name") ?? bid, faction, family, modRows);
            emblemsByRow[bt.Index] = info;
            emblemsByHash[info.TypeHash] = info;
        }
        EmblemsByRow = emblemsByRow;
        EmblemsByHash = emblemsByHash;
        LoadVoices();
    }
}
