using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>
/// Zana &amp; Eagon Memory Tears storyline (<c>ThreadsOfTheOriginator</c> Part A, states 20–71), Originator Memory Line
/// chains (<c>S→C 0x01a3</c> section <c>0x08</c> BE, <c>0x7b 0x42</c>, <c>C→S 0x0297</c>), the three Incarnation pinnacle
/// fights, <c>EndgameMemoryTearBenevolenceQuest</c> ("Colourful Roses"), the <c>MapWorldsMemoryTear</c> finale, and
/// Originator-influenced maps (<c>MapZanaInfluenced</c>).
/// </summary>
public static class MemoryTears
{
    /// <summary>S→C 0x007b kind 0x42: Eagon's Memory Lines Map Device UI (inventory mode 0x5e, MemoryLineMaps1).</summary>
    public const byte MemoryLinesMapDeviceUi = 0x42;

    /// <summary>Sentinel <see cref="MapOpening.Target"/> value set by <c>PortalsFeature.ActivateMemoryLine</c> (C→S 0x0297).</summary>
    public const ushort MemoryLineActivateTarget = 0xFFFF;

    public const string BeginMemoryLineObjectPath = "Metadata/Terrain/EndGame/MapAtlasMemory/Objects/BeginMemoryLineObject";
    public const string MemoryTearTelepadPath = "Metadata/MiscellaneousObjects/Memories_of_Zana/MemoryTearTelepad";
    public const string CollectableBlueRosePath = "Metadata/Monsters/AtlasMemories/BenevolenceBoss/Arena/Collectable1";
    public const string CollectableRedRosePath = "Metadata/Monsters/AtlasMemories/BenevolenceBoss/Arena/Collectable2";
    public const string EagonWildPath = "Metadata/NPC/Epilogue/EagonWild";
    public const string EagonFragmentedMemoryPath = "Metadata/NPC/Epilogue/EagonFragmentedMemory";
    public const string ZanaFragmentedMemoryPath = "Metadata/NPC/Epilogue/ZanaFragmentedMemory";

    public const string CourtyardOfWastingArea = "MapWorldsCourtyardOfWasting";
    public const string ChambersOfImpurityArea = "MapWorldsChambersOfImpurity";
    public const string TheatreOfLiesArea = "MapWorldsTheatreOfLies";
    public const string MomentOfLonelinessArea = "MapWorldsMemoryBoss2";
    public const string MomentOfTraumaArea = "MapWorldsMemoryBoss1";
    public const string MomentOfReverenceArea = "MapWorldsMemoryBoss3";
    public const string MemoryTearArea = "MapWorldsMemoryTear";

    public const string NeglectedFlameVariety = "Metadata/Monsters/AtlasMemory/FragmentOfIgnorance/FragmentOfIgnorance__";
    public const string CardinalOfFearVariety = "Metadata/Monsters/AtlasMemory/FragmentOfAnger";
    public const string DeceitfulGodVariety = "Metadata/Monsters/AtlasMemory/FragmentOfBenevolence";
    public const string IncarnationOfNeglectVariety = "Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBoss";
    public const string IncarnationOfNeglectUberVariety = "Metadata/Monsters/AtlasMemory/IgnoranceBoss/IgnoranceBossUBER";
    public const string IncarnationOfFearVariety = "Metadata/Monsters/AtlasMemory/AngerBoss";
    public const string IncarnationOfFearUberVariety = "Metadata/Monsters/AtlasMemory/AngerBossUBER";
    public const string IncarnationOfDreadVariety = "Metadata/Monsters/AtlasMemory/BenevolenceBoss";
    public const string IncarnationOfDreadUberVariety = "Metadata/Monsters/AtlasMemory/BenevolenceBossUBER";

    // LeagueQuestFlags for ThreadsOfTheOriginator Part A
    public const int LqfEagonWildSpotted = 1480;
    public const int LqfMemoryTearUsed = 1481;
    public const int LqfEagonHideoutUnlocked = 1492;
    public const int LqfEagonWildIntro = 1493;
    public const int LqfEagonMemoryQuestStartSeen = 1494;

    public const int LqfMemoryLineZana1 = 1474;
    public const int LqfMemoryLineZana2 = 1475;
    public const int LqfMemoryLineZana3 = 1476;
    public const int LqfMemoryLineZana1Quest = 1477;
    public const int LqfMemoryLineZana2Quest = 1478;
    public const int LqfMemoryLineZana3Quest = 1479;
    public const int LqfMemoryLineZana1QuestRepeat = 1558;
    public const int LqfMemoryLineZana2QuestRepeat = 1559;
    public const int LqfMemoryLineZana3QuestRepeat = 1560;
    public const int LqfCourtyardReady = 1568;
    public const int LqfTheatreReady = 1569;
    public const int LqfChambersReady = 1570;

    public const int LqfFirstMinibossDefeated = 1483;
    public const int LqfSecondMinibossDefeated = 1487;
    public const int LqfThirdMinibossDefeated = 1491;
    public const int LqfMemorySiphoned1 = 1565;
    public const int LqfMemorySiphoned2 = 1566;
    public const int LqfMemorySiphoned3 = 1567;

    public const int LqfNeglectDefeated = 704;
    public const int LqfFearDefeated = 1488;
    public const int LqfDreadDefeated = 703;
    public const int LqfOriginatorVoidstoneUnlocked = 709;
    public const int LqfAtlasDestructionHasBegun = 1564;
    public const int LqfDestroyingAtlas1 = 1547;
    public const int LqfDestroyingAtlas2 = 1548;

    // QuestFlags for Colourful Roses (Quest 205)
    public const string QfMemoryBossCollectBothRoses = "MemoryBossCollectBothRoses"; // 3319
    public const string QfBlueRoseHeld = "BlueRoseHeld";                             // 3320
    public const string QfRedRoseHeld = "RedRoseHeld";                               // 3321
    public const string QfBlueRoseReturned = "BlueRoseReturned";                     // 3318
    public const string QfRedRoseReturned = "RedRoseReturned";                       // 3322

    // QuestFlags for ThreadsOfTheOriginator Part A encounters
    public const string QfIsMapOwner = "IsMapOwner";
    public const string QfFirstMemoryBossSeen = "FirstMemoryBossSeen";
    public const string QfSecondMemoryBossSeen = "SecondMemoryBossSeen";
    public const string QfThirdMemoryBossSeen = "ThirdMemoryBossSeen";
    public const string QfNeglectMapPortalsActive = "NeglectMapPortalsActive";
    public const string QfEnteredFirstPinnacleBossArea = "EnteredFirstPinnacleBossArea";
    public const string QfFirstMemoryBossPinnacleSeen = "FirstMemoryBossPinnacleSeen";
    public const string QfWitnessZana1 = "WitnessZana1";
    public const string QfAngerMapPortalsActive = "AngerMapPortalsActive";
    public const string QfEnteredSecondPinnacleBossArea = "EnteredSecondPinnacleBossArea";
    public const string QfSecondMemoryBossPinnacleSeen = "SecondMemoryBossPinnacleSeen";
    public const string QfWitnessZana2 = "WitnessZana2";
    public const string QfBenevolenceMapPortalsActive = "BenevolenceMapPortalsActive";
    public const string QfEnteredThirdPinnacleBossArea = "EnteredThirdPinnacleBossArea";
    public const string QfThirdMemoryBossPinnacleSeen = "ThirdMemoryBossPinnacleSeen";
    public const string QfMemoryTearMapPortalsActive = "MemoryTearMapPortalsActive";
    public const string QfMapWorldsMemoryTearEntered = "MapWorldsMemoryTearEntered";
    public const string QfWitnessZana3 = "WitnessZana3";
    public const string QfWitnessZana3_2 = "WitnessZana3_2";

    const string MemoryLinesKey = "atlas.memoryLines";
    const string NextLineIdKey = "atlas.memoryLineNextId";

    public sealed record MemoryLineTypeInfo(
        int Row, string Id, ushort Hash16, int Lqf, string Suffix,
        int MinSteps, int MaxSteps, int FinalAtlasNodeRow, string FinalAreaId, int Tier,
        int? CurrencyItemRow, IReadOnlyList<(uint StatWireKey, int Value)> Stats);

    public sealed record SavedMemoryLine(
        uint LineId, ushort StartNodeHash16, string TypeId, ushort TypeHash16,
        byte CurrentStepIndex, byte TotalSteps, List<string> StepNodeIds, string FinalAreaId, int Tier);

    public sealed record AreaInfo(string Id, ushort Hash16, string Name, int Level);

    static Dictionary<string, MemoryLineTypeInfo> _typesById = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<int, MemoryLineTypeInfo> _typesByCurrencyRow = [];
    static Dictionary<string, AreaInfo> _areasById = new(StringComparer.OrdinalIgnoreCase);

    static readonly string[] MemoryEncounterAreas =
    [
        CourtyardOfWastingArea, ChambersOfImpurityArea, TheatreOfLiesArea,
        MomentOfLonelinessArea, MomentOfTraumaArea, MomentOfReverenceArea, MemoryTearArea,
    ];

    static readonly string[] FlagMapOwner = [QfIsMapOwner];
    static readonly string[] FlagNone = [];

    // Per-instance tear object tracking (objectId -> tear state)
    sealed class TearInstanceState
    {
        public uint TearObjectId;
        public int TearX, TearY;
        public uint EagonNpcId;
        public bool TearUsed;
        public int Stage; // 1, 2, 3, or 4 (repeatable)
    }

    // Per-instance Colourful Roses state inside MapWorldsMemoryBoss3
    public sealed class ColourfulRosesState
    {
        public uint BlueRoseId;
        public int BlueX, BlueY;
        public bool BlueCollected;
        public bool BlueReturned;
        public uint RedRoseId;
        public int RedX, RedY;
        public bool RedCollected;
        public bool RedReturned;
        public bool Initialized;
    }

    // Per-instance MemoryTear finale state
    sealed class FinaleInstanceState
    {
        public uint EagonId;
        public uint ZanaId;
        public bool Completed;
    }

    static readonly Store<InstanceState, TearInstanceState> TearInstances = new();
    static readonly Store<InstanceState, ColourfulRosesState> RoseInstances = new();
    static readonly Store<InstanceState, FinaleInstanceState> FinaleInstances = new();

    public static void LoadData()
    {
        var areas = new Dictionary<string, AreaInfo>(StringComparer.OrdinalIgnoreCase);
        var areaByRow = new Dictionary<int, AreaInfo>();
        if (ClientTables.Has("WorldAreas"))
        {
            int idx = 0;
            foreach (var row in ClientTables.Table("WorldAreas"))
            {
                string id = row.Text("Id") ?? "";
                if (id.Length > 0 && row.IntOrNull("HASH16") is int hash)
                {
                    var info = new AreaInfo(id, (ushort)hash, row.Text("Name") ?? id, row.IntOrNull("AreaLevel") ?? 80);
                    areas[id] = info;
                    areaByRow[idx] = info;
                }
                idx++;
            }
        }
        _areasById = areas;

        var atlasNodeAreas = new Dictionary<int, string>();
        if (ClientTables.Has("AtlasNode"))
        {
            int idx = 0;
            foreach (var row in ClientTables.Table("AtlasNode"))
            {
                int areaRow = row.IntOrNull("Area1") ?? row.IntOrNull("Area2") ?? -1;
                if (areaRow >= 0 && areaByRow.TryGetValue(areaRow, out var info))
                    atlasNodeAreas[idx] = info.Id;
                idx++;
            }
        }

        var byId = new Dictionary<string, MemoryLineTypeInfo>(StringComparer.OrdinalIgnoreCase);
        var byCurrency = new Dictionary<int, MemoryLineTypeInfo>();
        if (ClientTables.Has("MemoryLineType"))
        {
            int idx = 0;
            foreach (var row in ClientTables.Table("MemoryLineType"))
            {
                string id = row.Text("Id") ?? "";
                ushort hash = (ushort)(row.IntOrNull("HASH16") ?? 0);
                int lqf = row.IntOrNull("__col5") ?? 0;
                string suffix = row.Text("Suffix") ?? "";
                int minSteps = Math.Max(1, row.IntOrNull("__col7") ?? 4);
                int maxSteps = Math.Max(minSteps, row.IntOrNull("__col8") ?? minSteps);
                int finalNodeRow = row.IntOrNull("FinalAtlasNode") ?? -1;
                string finalArea = atlasNodeAreas.GetValueOrDefault(finalNodeRow, CourtyardOfWastingArea);
                int tier = row.IntOrNull("Tier") ?? 16;
                int? currencyRow = row.IntOrNull("CurrencyItem");
                int[] statRows = [.. row.Ints("Stats")];
                int[] statVals = [.. row.Ints("StatValues")];
                var stats = new List<(uint, int)>();
                for (int i = 0; i < Math.Min(statRows.Length, statVals.Length); i++)
                    if (statRows[i] >= 0) stats.Add(((uint)statRows[i] + 1, statVals[i]));
                var info = new MemoryLineTypeInfo(idx, id, hash, lqf, suffix, minSteps, maxSteps, finalNodeRow, finalArea, tier, currencyRow, stats);
                if (id.Length > 0) byId[id] = info;
                if (currencyRow is int cr) byCurrency[cr] = info;
                idx++;
            }
        }
        _typesById = byId;
        _typesByCurrencyRow = byCurrency;
    }

    public static void Register(string owner)
    {
        ObjectTypes.RegisterOverlay(owner, new ObjectTypes.TypeOverlay(
            BeginMemoryLineObjectPath,
            ["Positioned", "BaseEvents", "InteractionAction", "StateMachine", "Animated", "MinimapIcon"],
            new StateMachineData([1, 1], 0, ["active", "targetable"]),
            ObjectSize: 3,
            Blocking: false,
            Model: "Metadata/Effects/Environment/Memories_of_Zana/Memory_tear.ao"));

        GameEvents.AreaQuestFlags.Add(owner, TransientAreaFlags);
        GameEvents.MapOpening.Add(owner, OnMapOpening);
        GameEvents.PlayerEntered.Add(owner, OnPlayerEntered);
        GameEvents.ObjectUse.Add(owner, OnObjectUse);
        GameEvents.MonsterKilled.Add(owner, kill => OnMonsterKilled(kill.Player, kill.Monster));
        GameEvents.Kill.Add(owner, kill =>
        {
            if (kill.PlayerKill) return;
            if (!IsTrackedMemoryMonster(kill.VarietyId)) return;
            var monster = global::Game.ModMonster(kill.Monster, kill.At);
            kill.Later(() => OnMonsterKilled(kill.Player, monster));
        });
        GameEvents.AreaTick.Add(owner, player =>
            player.Layout.Id is MomentOfReverenceArea or "MapWorldsMemoryBoss3_Uber"
                ? (p, _) => TickColourfulRoses(p)
                : null);
        GameEvents.RulesetEvent.Add(owner, OnRulesetEvent);
        GameEvents.NpcOptionPicked.Add(owner, OnNpcOptionPicked);
    }

    static bool IsTrackedMemoryMonster(string varietyId) => varietyId is
        NeglectedFlameVariety or CardinalOfFearVariety or DeceitfulGodVariety
        or IncarnationOfNeglectVariety or IncarnationOfNeglectUberVariety
        or IncarnationOfFearVariety or IncarnationOfFearUberVariety
        or IncarnationOfDreadVariety or IncarnationOfDreadUberVariety;

    static IReadOnlyList<string> TransientAreaFlags(Player player)
    {
        if (MemoryEncounterAreas.Contains(player.Layout.Id, StringComparer.OrdinalIgnoreCase)
            || (MapDevice.ForInstance(player) is { } run
                && string.Equals(run.Owner, CharacterName(player), StringComparison.OrdinalIgnoreCase)))
            return FlagMapOwner;
        return FlagNone;
    }

    // ---- Saved Memory Lines (S→C 0x01a3 section 0x08) ------------------------------------------------------------------

    public static List<SavedMemoryLine> LoadLines(ISavedData data)
    {
        string? json = data.Get(MemoryLinesKey);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<SavedMemoryLine>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void SaveLines(ISavedData data, IReadOnlyList<SavedMemoryLine> lines) =>
        data.Change(MemoryLinesKey, _ => JsonSerializer.Serialize(lines));

    static uint AllocateLineId(ISavedData data)
    {
        uint allocated = 1;
        data.Change(NextLineIdKey, old =>
        {
            uint next = uint.TryParse(old, out uint cur) && cur > 0 ? cur + 1 : 1;
            allocated = next;
            return next.ToString();
        });
        return allocated;
    }

    public static IReadOnlyList<AtlasProgress.MemoryLineWire> ActiveLinesWire(ISavedData data) =>
        [.. LoadLines(data).Select(l => new AtlasProgress.MemoryLineWire(
            l.LineId, l.StartNodeHash16, l.TypeHash16, l.CurrentStepIndex, l.TotalSteps))];

    public static Task SendMemoryLines(Player player)
    {
        var modPlayer = PlayerOf(player);
        var data = WorldOf(modPlayer).CharacterData;
        var slots = AtlasFeature.SlotsOf(modPlayer);
        var (tangle, cleansing) = AtlasFeature.PrimordialTrackerCounts(data);
        var lines = ActiveLinesWire(data);
        return Send(player, AtlasProgress.MemoryLinesMessage(lines, AtlasProgress.VoidstoneMask(slots), tangle, cleansing,
            AtlasProgress.ScryPairs(data)),
            $"0x01a3 Atlas memory lines ({lines.Count} active)");
    }

    public static SavedMemoryLine? CreateLine(ISavedData data, string typeId, AtlasProgress.Node? startNode = null, int? stepCountOverride = null)
    {
        if (!_typesById.TryGetValue(typeId, out var type)) return null;
        var normalMaps = AtlasProgress.Maps.Where(n => n.IsNormalMap && !n.RequiresSpecificItem).ToList();
        var start = startNode ?? normalMaps.FirstOrDefault();
        if (start is null) return null;
        int totalSteps = Math.Clamp(stepCountOverride ?? type.MinSteps, 2, Math.Max(2, type.MaxSteps));
        var stepIds = new List<string> { start.Id };
        foreach (var candidate in normalMaps)
        {
            if (stepIds.Count >= totalSteps - 1) break;
            if (candidate.Id != start.Id && candidate.Region == start.Region)
                stepIds.Add(candidate.Id);
        }
        foreach (var candidate in normalMaps)
        {
            if (stepIds.Count >= totalSteps - 1) break;
            if (!stepIds.Contains(candidate.Id))
                stepIds.Add(candidate.Id);
        }
        stepIds.Add(type.FinalAreaId);

        SavedMemoryLine? created = null;
        Saves.Atomically(() =>
        {
            var lines = LoadLines(data);
            lines.RemoveAll(l => string.Equals(l.TypeId, type.Id, StringComparison.OrdinalIgnoreCase));
            uint id = AllocateLineId(data);
            created = new SavedMemoryLine(id, start.Hash16, type.Id, type.Hash16, 0, (byte)stepIds.Count, stepIds, type.FinalAreaId, type.Tier);
            lines.Add(created);
            SaveLines(data, lines);
        });
        return created;
    }

    /// <summary>Creates a repeatable Originator Memory Line when applying a Memory Thread (4687..4689) via C→S 0x0075/0x0076.</summary>
    public static (SavedMemoryLine Line, int UnlockLqf)? CreateLineFromThread(ISavedData data, AtlasProgress.Node startNode, int currencyItemRow)
    {
        if (!_typesByCurrencyRow.TryGetValue(currencyItemRow, out var type)) return null;
        var line = CreateLine(data, type.Id, startNode);
        return line is null ? null : (line, type.Lqf);
    }

    // ---- Memory Tear Qualification & Map Opening -----------------------------------------------------------------------

    /// <summary>Determines which Memory Tear story stage (1, 2, 3, or 4 repeatable) a map of <paramref name="tier"/> qualifies for, or 0 if none.</summary>
    public static int EligibleTearStage(Player player, int tier)
    {
        if (!player.Character.HasQuestFlag(Quests.EndGameAvailable)) return 0;
        var lqfs = player.Character.LeagueQuestFlags;
        if (tier >= 14 && !lqfs.Contains(LqfEagonHideoutUnlocked) && !lqfs.Contains(LqfNeglectDefeated))
            return 1;
        if (tier >= 15 && lqfs.Contains(LqfNeglectDefeated) && !lqfs.Contains(LqfFearDefeated)
            && !lqfs.Contains(LqfMemoryLineZana2Quest) && !lqfs.Contains(LqfSecondMinibossDefeated))
            return 2;
        if (tier >= 16 && lqfs.Contains(LqfFearDefeated) && !lqfs.Contains(LqfDreadDefeated)
            && !lqfs.Contains(LqfMemoryLineZana3Quest) && !lqfs.Contains(LqfThirdMinibossDefeated))
            return 3;
        if (tier >= 16 && lqfs.Contains(LqfAtlasDestructionHasBegun))
            return 4;
        return 0;
    }

    static void OnMapOpening(MapOpening request)
    {
        if (request.Campaign) return;
        var player = request.Player;
        var data = WorldOf(PlayerOf(player)).CharacterData;

        // 1. Handle C→S 0x0297 Memory Line device activation or MemoryLine items placed in the Map Device.
        bool isMemoryDevice = request.Target == MemoryLineActivateTarget;
        var memoryItem = request.Slots.FirstOrDefault(s => s.Class == MapDevice.MemoryLineClass);
        if (memoryItem.Class == MapDevice.MemoryLineClass)
        {
            ResolveMemoryLineItemOpening(request, memoryItem);
            return;
        }
        if (isMemoryDevice && request.Opening is null)
        {
            ResolveActiveOriginatorMemoryOpening(request, data);
            return;
        }

        if (request.Opening is not { } opening) return;

        // 2. Originator's Memories (MapZanaInfluenced): ensure implicit/mod Zana influence carries petal stats.
        if (opening.Mods.Get(Stat.MapZanaInfluence) > 0)
        {
            var extra = new Dictionary<uint, int>
            {
                [Stat.MapZanaInfluence] = Math.Max(1, opening.Mods.Get(Stat.MapZanaInfluence)),
                [Stat.MapZanaInfluenceDoodadDensity] = Math.Max(5, opening.Mods.Get(Stat.MapZanaInfluenceDoodadDensity)),
                [Stat.DropZanaInfluencePetalsOnDeath] = 1,
            };
            opening = opening with { Mods = opening.Mods.WithFragmentStats(extra) };
            request.Opening = opening;
        }

        // 3. Memory Tear spawns in qualifying T14+/T15+/T16+ normal Atlas maps.
        if (opening.Area.UsesAtlas && opening.Mods.MapRarity != ItemRarity.Unique)
        {
            int stage = EligibleTearStage(player, opening.Area.Tier);
            bool spawnTear = stage is 1 or 2 or 3
                || (stage == 4 && (opening.Mods.Get(Stat.MapSpawnMemoryLineInitiator) > 0
                    || LoadLines(data).Count == 0 && Random.Shared.Next(100) < 25));
            if (spawnTear)
            {
                var content = opening.ContentData is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(opening.ContentData, StringComparer.Ordinal);
                content["memory.tearStage"] = stage.ToString();
                request.Opening = opening with
                {
                    Mods = opening.Mods.WithFragmentStat(Stat.MapSpawnMemoryLineInitiator, 1),
                    ContentData = content,
                };
            }
        }
    }

    static void ResolveMemoryLineItemOpening(MapOpening request, MapDevice.Slot slot)
    {
        var layout = MapDevices.Atlas?.Selected(CharacterName(request.Player)) ?? MapDevices.Atlas?.Default;
        if (layout is null)
        {
            request.Reason = "No Atlas layout is available for this Memory Line.";
            return;
        }
        int tier = 16;
        var area = new MapDevice.MapArea(layout.Hash16, layout.Id, $"{layout.Name} ({slot.Name})", tier)
        {
            Level = MapDevice.MapLevel(tier),
            UsesAtlas = true,
        };
        var extraStats = new Dictionary<uint, int>
        {
            [Stat.MapZanaInfluence] = 1,
            [Stat.MapZanaInfluenceDoodadDensity] = 6,
            [Stat.DropZanaInfluencePetalsOnDeath] = 1,
            [Stat.MemoryLineStepCount] = 4,
            [Stat.MapItemDropQuantityPlusPct] = 40,
            [Stat.MapPackSizePlusPct] = 20,
        };
        if (slot.BaseId.Contains("Alva", StringComparison.OrdinalIgnoreCase))
            extraStats[Stat.MapIncursionLeague] = 1;
        else if (slot.BaseId.Contains("Kirac", StringComparison.OrdinalIgnoreCase))
            extraStats[Stat.MemoryLineNumberOfShrines] = 4;
        var mods = new MapMods(extraStats) { Implicit = extraStats, MapRarity = ItemRarity.Rare };
        request.Opening = new MapDevice.Opening(area, mods, slot.BaseRow)
        {
            ContentData = new Dictionary<string, string> { ["memory.itemId"] = slot.BaseId },
        };
        request.Reason = "";
    }

    static void ResolveActiveOriginatorMemoryOpening(MapOpening request, ISavedData data)
    {
        var lines = LoadLines(data);
        if (lines.Count == 0)
        {
            request.Reason = "You have no active Originator Memory Line. Speak to Eagon or find a Memory Tear in T14+ maps.";
            return;
        }
        var active = lines[0];
        bool isFinal = active.CurrentStepIndex >= active.TotalSteps - 1;
        MapDevice.MapArea? area = null;
        if (isFinal && _areasById.TryGetValue(active.FinalAreaId, out var finalInfo))
        {
            area = new MapDevice.MapArea(finalInfo.Hash16, finalInfo.Id, finalInfo.Name, Math.Clamp(active.Tier, 14, 16))
            {
                Level = Math.Max(finalInfo.Level, MapDevice.MapLevel(active.Tier)),
                UsesAtlas = true,
            };
        }
        else if (active.CurrentStepIndex < active.StepNodeIds.Count
            && AtlasProgress.Find(active.StepNodeIds[active.CurrentStepIndex]) is { } node
            && node.Areas.FirstOrDefault() is { } stepAreaId
            && _areasById.TryGetValue(stepAreaId, out var stepInfo))
        {
            area = new MapDevice.MapArea(stepInfo.Hash16, stepInfo.Id, stepInfo.Name, Math.Clamp(active.Tier, 14, 16))
            {
                Level = MapDevice.MapLevel(Math.Clamp(active.Tier, 14, 16)),
                UsesAtlas = true,
            };
        }
        if (area is null)
        {
            request.Reason = "Unable to resolve the next area in your Memory Line.";
            return;
        }
        var extraStats = new Dictionary<uint, int>
        {
            [Stat.MapZanaInfluence] = 1,
            [Stat.MapZanaInfluenceDoodadDensity] = 6,
            [Stat.DropZanaInfluencePetalsOnDeath] = 1,
            [Stat.MapItemDropQuantityPlusPct] = 35,
            [Stat.MapPackSizePlusPct] = 15,
        };
        if (_typesById.TryGetValue(active.TypeId, out var typeInfo))
            foreach (var (statKey, statVal) in typeInfo.Stats)
                extraStats[statKey] = extraStats.GetValueOrDefault(statKey) + statVal;

        var mods = new MapMods(extraStats) { Implicit = extraStats, MapRarity = ItemRarity.Rare };
        request.Opening = new MapDevice.Opening(area, mods)
        {
            ContentData = new Dictionary<string, string>
            {
                ["memory.lineId"] = active.LineId.ToString(),
                ["memory.typeId"] = active.TypeId,
                ["memory.step"] = active.CurrentStepIndex.ToString(),
                ["memory.final"] = isFinal ? "1" : "0",
            },
        };
        if (request.Slots.Count == 0)
            request.ConsumeSlots = [];
        request.Reason = "";
    }

    // ---- Ruleset Events (Eagon Hideout dialogues) ----------------------------------------------------------------------

    static async Task<bool> OnRulesetEvent(Player player, uint npcId, SpawnedNpc npc, NpcCatalog.Talk talk, string ev)
    {
        if (string.Equals(ev, "open_memory_line", StringComparison.OrdinalIgnoreCase))
        {
            await SendMemoryLines(player);
            await Send(player, InstanceWaypoints.OpenUi(MemoryLinesMapDeviceUi, npcId),
                $"0x007b open Eagon Memory Lines Map Device (NPCTalk {talk.Row})");
            return true;
        }
        if (string.Equals(ev, "open_memory_boss_quest", StringComparison.OrdinalIgnoreCase))
        {
            await OpenMemoryBossQuest(player, talk);
            return true;
        }
        return false;
    }

    internal static async Task<bool> OpenMemoryBossQuest(Player player, NpcCatalog.Talk? talk = null)
    {
        var lqfs = player.Character.LeagueQuestFlags;
        (string AreaId, string PortalQf, string Label)? chosen = talk?.Row switch
        {
            3686 => (MomentOfLonelinessArea, QfNeglectMapPortalsActive, "Moment of Loneliness"),
            3687 => (MomentOfTraumaArea, QfAngerMapPortalsActive, "Moment of Trauma"),
            3688 => (MomentOfReverenceArea, QfBenevolenceMapPortalsActive, "Moment of Reverence"),
            3689 => (MemoryTearArea, QfMemoryTearMapPortalsActive, "Memory Tear"),
            _ => ResolveBossQuestByFlags(lqfs),
        };
        if (chosen is null || !_areasById.TryGetValue(chosen.Value.AreaId, out var areaInfo))
        {
            await PlayerOf(player).Tell("Eagon has no Memory encounter ready to open right now.");
            return false;
        }
        var mapArea = new MapDevice.MapArea(areaInfo.Hash16, areaInfo.Id, areaInfo.Name, 16)
        {
            Level = areaInfo.Level,
            UsesAtlas = false,
        };
        bool opened = false;
        if (MapDevice.OpenPortalsFromDevice is { } opener)
            opened = await opener(player, mapArea, MapMods.None, new Dictionary<string, string> { ["memory.bossQuest"] = areaInfo.Id }, MapDevice.PortalCount) is not null;
        if (!opened)
        {
            var (px, py) = player.Layout.Near((player.Layout.SpawnX + 8, player.Layout.SpawnY));
            opened = await global::Game.SpawnCustomPortal(player, px, py, areaInfo.Hash16,
                new PortalOptions(UsesLeft: MapDevice.PortalCount, SpawnReturnPortal: true, Owner: CharacterName(player))) is not null;
        }
        if (!opened) return false;
        if (!ClientHasQuestFlag(player, chosen.Value.PortalQf))
            await SetQuestFlags(player, [chosen.Value.PortalQf]);
        await PlayerOf(player).Tell($"Eagon opens portals to {chosen.Value.Label}.");
        return true;
    }

    static (string AreaId, string PortalQf, string Label)? ResolveBossQuestByFlags(HashSet<int> lqfs)
    {
        if (lqfs.Contains(LqfDreadDefeated) && !lqfs.Contains(LqfAtlasDestructionHasBegun))
            return (MemoryTearArea, QfMemoryTearMapPortalsActive, "Memory Tear");
        if (lqfs.Contains(LqfThirdMinibossDefeated) && !lqfs.Contains(LqfDreadDefeated))
            return (MomentOfReverenceArea, QfBenevolenceMapPortalsActive, "Moment of Reverence");
        if (lqfs.Contains(LqfSecondMinibossDefeated) && !lqfs.Contains(LqfFearDefeated))
            return (MomentOfTraumaArea, QfAngerMapPortalsActive, "Moment of Trauma");
        if (lqfs.Contains(LqfFirstMinibossDefeated) && !lqfs.Contains(LqfNeglectDefeated))
            return (MomentOfLonelinessArea, QfNeglectMapPortalsActive, "Moment of Loneliness");
        return null;
    }

    // ---- PlayerEntered: Memory Tear Spawns, Boss Arenas, Colourful Roses, Zana Finale ----------------------------------

    static async Task OnPlayerEntered(Player player)
    {
        string areaId = player.Layout.Id;
        var run = MapDevice.ForInstance(player);

        // 1. Memory Tear spawn inside a qualifying map
        if (run?.ContentData is { } content && content.TryGetValue("memory.tearStage", out string? stageStr)
            && int.TryParse(stageStr, out int stage) && stage > 0)
        {
            await SpawnMemoryTearEncounter(player, stage);
        }

        // 2. Miniboss areas (Courtyard of Wasting / Chambers of Impurity / Theatre of Lies)
        if (areaId == CourtyardOfWastingArea && !ClientHasQuestFlag(player, QfFirstMemoryBossSeen))
            await SetQuestFlags(player, [QfFirstMemoryBossSeen]);
        else if (areaId == ChambersOfImpurityArea && !ClientHasQuestFlag(player, QfSecondMemoryBossSeen))
            await SetQuestFlags(player, [QfSecondMemoryBossSeen]);
        else if (areaId == TheatreOfLiesArea && !ClientHasQuestFlag(player, QfThirdMemoryBossSeen))
            await SetQuestFlags(player, [QfThirdMemoryBossSeen]);

        // 3. Incarnation Pinnacle Arenas
        if (areaId is MomentOfLonelinessArea or "MapWorldsMemoryBoss2_Uber")
        {
            if (ClientHasQuestFlag(player, QfNeglectMapPortalsActive))
                await SetQuestFlags(player, [QfNeglectMapPortalsActive], on: false);
            await SetQuestFlags(player, [QfEnteredFirstPinnacleBossArea, QfFirstMemoryBossPinnacleSeen]);
        }
        else if (areaId is MomentOfTraumaArea or "MapWorldsMemoryBoss1_Uber")
        {
            if (ClientHasQuestFlag(player, QfAngerMapPortalsActive))
                await SetQuestFlags(player, [QfAngerMapPortalsActive], on: false);
            await SetQuestFlags(player, [QfEnteredSecondPinnacleBossArea, QfSecondMemoryBossPinnacleSeen]);
        }
        else if (areaId is MomentOfReverenceArea or "MapWorldsMemoryBoss3_Uber")
        {
            if (ClientHasQuestFlag(player, QfBenevolenceMapPortalsActive))
                await SetQuestFlags(player, [QfBenevolenceMapPortalsActive], on: false);
            await SetQuestFlags(player, [QfEnteredThirdPinnacleBossArea, QfThirdMemoryBossPinnacleSeen]);
            await InitColourfulRoses(player);
        }

        // 4. MapWorldsMemoryTear Finale
        if (areaId == MemoryTearArea)
            await EnterMemoryTearFinale(player);
    }

    static async Task SpawnMemoryTearEncounter(Player player, int stage)
    {
        if (player.Instance is not { } instance) return;
        var state = TearInstances.GetOrCreateValue(instance);
        state.Stage = stage;
        if (state.TearObjectId != 0) return;

        var (ex, ey) = player.Layout.Near((player.Layout.SpawnX + 18, player.Layout.SpawnY + 6));
        var (tx, ty) = player.Layout.Near((ex + 8, ey));

        // Spawn EagonWild (NPC 709) with StateMachine activated=1, targetable=1 so he is visible & interactive
        if (global::Game.Npcs.Find(EagonWildPath) is { Supported: true } eagonNpc)
        {
            var sm = (eagonNpc.StateMachine ?? new StateMachineData([0, 0, 0], 0, ["siphon", "activated", "targetable"]))
                .Set("activated", 1)
                .Set("targetable", 1);
            state.EagonNpcId = await global::Game.SpawnNpc(player, eagonNpc with { StateMachine = sm }, ex, ey, 0f);
            if (!player.Character.LeagueQuestFlags.Contains(LqfEagonWildSpotted))
                await SetLeagueQuestFlags(player, [LqfEagonWildSpotted]);
        }

        // Spawn BeginMemoryLineObject (Memory Tear rift)
        if (QuestObjectTypes.Find(BeginMemoryLineObjectPath) is { } tearInfo)
        {
            var tearSm = new StateMachineData([1, 1], 0, ["active", "targetable"]);
            if (QuestObject.CanSerialize(tearInfo.Components, haveStateMachine: true))
            {
                uint tearId = player.TakeObjectId();
                var obj = new QuestObject(tx, ty, tearInfo.Components, Rotation: 0f)
                {
                    StateMachine = tearSm,
                    Model = tearInfo.AnimatedModel,
                };
                await global::Game.SendWorldCreate(player, tearId,
                    () => InstanceEntities.CreateObject(tearId, InstanceEntities.ObjectTypeHash(BeginMemoryLineObjectPath), obj.Blob()),
                    $"0x02bb create Memory Tear 0x{tearId:x} at ({tx}, {ty})", wake: true);
                state.TearObjectId = tearId;
                state.TearX = tx;
                state.TearY = ty;
            }
        }
    }

    static async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance) return false;

        // 1. Clicking BeginMemoryLineObject (Memory Tear rift)
        if (TearInstances.TryGetValue(instance, out var tear) && tear.TearObjectId != 0 && use.TargetId == tear.TearObjectId)
        {
            if (tear.TearUsed) return true;
            if (!TryBeginObjectWalk(player, requestedAt, tear.TearX, tear.TearY, 4, out var walk)) return true;
            var (action, now, _, _, arrive) = walk;
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (!PlayerActionCurrent(player, action) || tear.TearUsed) return true;
            tear.TearUsed = true;

            if (QuestObjectTypes.Find(BeginMemoryLineObjectPath) is { } tearInfo)
            {
                int smIdx = tearInfo.Components.ToList().IndexOf("StateMachine");
                if (smIdx >= 0)
                {
                    var opened = new StateMachineData([2, 0], 0, ["active", "targetable"]);
                    await global::Game.SendWorldRecord(player, tear.TearObjectId,
                        InstanceEntities.StateMachineUpdate(tear.TearObjectId, (byte)smIdx, opened),
                        $"0x02bc Memory Tear 0x{tear.TearObjectId:x} opened (active=2)");
                }
            }
            await ActivateMemoryTearChain(player, tear.Stage);
            return true;
        }

        // 2. Clicking Blue/Red Rose directly in Moment of Reverence (fallback alongside walk-over pickup)
        if (RoseInstances.TryGetValue(instance, out var roses) && (use.TargetId == roses.BlueRoseId || use.TargetId == roses.RedRoseId))
        {
            bool isBlue = use.TargetId == roses.BlueRoseId;
            await TryCollectRose(player, roses, isBlue);
            return true;
        }

        // 3. Clicking ZanaFragmentedMemory in MapWorldsMemoryTear finale
        if (FinaleInstances.TryGetValue(instance, out var finale) && use.TargetId == finale.ZanaId && !finale.Completed)
        {
            await CompleteMemoryTearFinale(player, finale);
            return true;
        }

        return false;
    }

    /// <summary>Activates the next Memory Line chain when a player opens a Memory Tear in a map.</summary>
    public static async Task<SavedMemoryLine?> ActivateMemoryTearChain(Player player, int stage)
    {
        var data = WorldOf(PlayerOf(player)).CharacterData;
        var currentNode = AtlasProgress.OfArea(player.Layout.Id);
        string typeId = stage switch
        {
            1 => "MemoryLineZana1Quest",
            2 => "MemoryLineZana2Quest",
            3 => "MemoryLineZana3Quest",
            _ => new[] { "MemoryLineZana1", "MemoryLineZana2", "MemoryLineZana3" }[Random.Shared.Next(3)],
        };
        var line = CreateLine(data, typeId, currentNode);
        var lqfsToSet = new List<int> { LqfMemoryTearUsed };
        if (stage == 1) lqfsToSet.Add(LqfEagonMemoryQuestStartSeen);
        if (_typesById.TryGetValue(typeId, out var typeInfo) && typeInfo.Lqf > 0)
            lqfsToSet.Add(typeInfo.Lqf);
        var missing = lqfsToSet.Where(f => !player.Character.LeagueQuestFlags.Contains(f)).Distinct().ToList();
        if (missing.Count > 0) await SetLeagueQuestFlags(player, missing);
        await SendMemoryLines(player);
        await PlayerOf(player).Tell($"Memory Tear stabilized: Originator's Memory {typeId} added to Eagon's Map Device.");
        return line;
    }

    // ---- Eagon NPCTalk Progression -------------------------------------------------------------------------------------

    static async Task<bool> OnNpcOptionPicked(Player player, NpcCatalog.Talk talk)
    {
        // Eagon (707/708/709/714) heard league flags (NPCTalk col 15) advance Eagon's conversation chains like Kirac's.
        if (talk.Npc is >= 707 and <= 714 && talk.RewardOffer is null && talk.HeardLeagueFlags.Count > 0)
        {
            var missing = talk.HeardLeagueFlags.Where(f => f > 1 && !player.Character.LeagueQuestFlags.Contains(f)).ToList();
            if (missing.Count > 0) await SetLeagueQuestFlags(player, missing);
        }
        // EagonWild NPCTalk 3672 ("Thread Coordinates"): unlocks EagonHideout (LQF 1492) and ensures Chain 1 exists.
        if (talk.Row == 3672)
        {
            var data = WorldOf(PlayerOf(player)).CharacterData;
            if (LoadLines(data).Count == 0 && !player.Character.LeagueQuestFlags.Contains(LqfNeglectDefeated))
                await ActivateMemoryTearChain(player, 1);
            if (!player.Character.LeagueQuestFlags.Contains(LqfEagonHideoutUnlocked))
                await SetLeagueQuestFlags(player, [LqfEagonHideoutUnlocked]);
        }
        // EagonFragmentedMemory finale interaction inside MapWorldsMemoryTear
        if (player.Layout.Id == MemoryTearArea && talk.Npc is 712 or 713 or 720
            && player.Instance is { } inst && FinaleInstances.TryGetValue(inst, out var finale) && !finale.Completed)
        {
            await CompleteMemoryTearFinale(player, finale);
        }
        return true;
    }

    // ---- Colourful Roses (Quest 205: EndgameMemoryTearBenevolenceQuest) ------------------------------------------------

    public static async Task<ColourfulRosesState> InitColourfulRoses(Player player)
    {
        var instance = player.Instance;
        var state = instance is not null ? RoseInstances.GetOrCreateValue(instance) : new ColourfulRosesState();
        if (state.Initialized) return state;
        state.Initialized = true;

        var clear = new[] { QfBlueRoseHeld, QfRedRoseHeld, QfBlueRoseReturned, QfRedRoseReturned }
            .Where(f => ClientHasQuestFlag(player, f)).ToList();
        if (clear.Count > 0) await SetQuestFlags(player, clear, on: false);
        if (!ClientHasQuestFlag(player, QfMemoryBossCollectBothRoses))
            await SetQuestFlags(player, [QfMemoryBossCollectBothRoses]);

        var (bx, by) = player.Layout.Near((player.Layout.SpawnX + 20, player.Layout.SpawnY - 12));
        var (rx, ry) = player.Layout.Near((player.Layout.SpawnX + 20, player.Layout.SpawnY + 12));
        state.BlueX = bx; state.BlueY = by;
        state.RedX = rx; state.RedY = ry;

        state.BlueRoseId = await SpawnRoseEntity(player, CollectableBlueRosePath, bx, by, "Blue Rose");
        state.RedRoseId = await SpawnRoseEntity(player, CollectableRedRosePath, rx, ry, "Red Rose");
        return state;
    }

    static async Task<uint> SpawnRoseEntity(Player player, string path, int x, int y, string label)
    {
        var info = QuestObjectTypes.Find(path);
        IReadOnlyList<string> components = info?.Components ?? ["Positioned", "BaseEvents", "Animated", "Functions", "StateMachine"];
        var sm = new StateMachineData([0, 0], 0, ["delete", "collected"]);
        if (!QuestObject.CanSerialize(components, haveStateMachine: true)) return 0;
        uint id = player.TakeObjectId();
        var obj = new QuestObject(x, y, components, Rotation: 0f) { StateMachine = sm };
        await global::Game.SendWorldCreate(player, id,
            () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(path), obj.Blob()),
            $"0x02bb create {label} 0x{id:x} at ({x}, {y})", wake: true);
        return id;
    }

    static async Task TickColourfulRoses(Player player)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || player.Instance is not { } instance) return;
        if (!RoseInstances.TryGetValue(instance, out var state) || !state.Initialized) return;
        if (state.BlueReturned && state.RedReturned) return;

        var (px, py) = player.Walk.Position(AreaTime(player));
        const int PickupRadiusSq = 12 * 12;
        const int ReturnRadiusSq = 24 * 24;

        bool holdingBlue = ClientHasQuestFlag(player, QfBlueRoseHeld);
        bool holdingRed = ClientHasQuestFlag(player, QfRedRoseHeld);

        if (!holdingBlue && !holdingRed)
        {
            if (!state.BlueCollected && DistSq(px, py, state.BlueX, state.BlueY) <= PickupRadiusSq)
                await TryCollectRose(player, state, isBlue: true);
            else if (!state.RedCollected && DistSq(px, py, state.RedX, state.RedY) <= PickupRadiusSq)
                await TryCollectRose(player, state, isBlue: false);
        }
        else
        {
            var boss = player.Area.Monsters.Values.FirstOrDefault(m =>
                !m.Dead && m.Life > 0 && m.Variety is { Id: IncarnationOfDreadVariety or IncarnationOfDreadUberVariety });
            if (boss is not null)
            {
                var (bx, by) = boss.Walk.Position(AreaTime(player));
                if (DistSq(px, py, bx, by) <= ReturnRadiusSq)
                    await TryReturnHeldRose(player, state);
            }
        }
    }

    public static async Task<bool> TryCollectRose(Player player, ColourfulRosesState state, bool isBlue)
    {
        if (ClientHasQuestFlag(player, QfBlueRoseHeld) || ClientHasQuestFlag(player, QfRedRoseHeld)) return false;
        if (isBlue ? state.BlueCollected : state.RedCollected) return false;
        if (isBlue) state.BlueCollected = true; else state.RedCollected = true;

        uint roseId = isBlue ? state.BlueRoseId : state.RedRoseId;
        string path = isBlue ? CollectableBlueRosePath : CollectableRedRosePath;
        await UpdateRoseState(player, roseId, path, delete: 0, collected: 1);
        await SetQuestFlags(player, [isBlue ? QfBlueRoseHeld : QfRedRoseHeld]);
        return true;
    }

    public static async Task<bool> TryReturnHeldRose(Player player, ColourfulRosesState state)
    {
        bool holdingBlue = ClientHasQuestFlag(player, QfBlueRoseHeld);
        bool holdingRed = ClientHasQuestFlag(player, QfRedRoseHeld);
        if (!holdingBlue && !holdingRed) return false;

        if (holdingBlue && !state.BlueReturned)
        {
            state.BlueReturned = true;
            await UpdateRoseState(player, state.BlueRoseId, CollectableBlueRosePath, delete: 1, collected: 1);
            await SetQuestFlags(player, [QfBlueRoseHeld], on: false);
            await SetQuestFlags(player, [QfBlueRoseReturned]);
        }
        else if (holdingRed && !state.RedReturned)
        {
            state.RedReturned = true;
            await UpdateRoseState(player, state.RedRoseId, CollectableRedRosePath, delete: 1, collected: 1);
            await SetQuestFlags(player, [QfRedRoseHeld], on: false);
            await SetQuestFlags(player, [QfRedRoseReturned]);
        }

        if (state.BlueReturned && state.RedReturned && ClientHasQuestFlag(player, QfMemoryBossCollectBothRoses))
            await SetQuestFlags(player, [QfMemoryBossCollectBothRoses], on: false);
        return true;
    }

    static async Task UpdateRoseState(Player player, uint roseId, string path, int delete, int collected)
    {
        if (roseId == 0) return;
        var components = QuestObjectTypes.Find(path)?.Components ?? ["Positioned", "BaseEvents", "Animated", "Functions", "StateMachine"];
        int smIdx = components.ToList().IndexOf("StateMachine");
        if (smIdx < 0) return;
        var sm = new StateMachineData([delete, collected], 0, ["delete", "collected"]);
        await global::Game.SendWorldRecord(player, roseId, InstanceEntities.StateMachineUpdate(roseId, (byte)smIdx, sm),
            $"0x02bc Rose 0x{roseId:x} state delete={delete} collected={collected}");
    }

    static int DistSq(int x1, int y1, int x2, int y2)
    {
        int dx = x1 - x2, dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    // ---- Memory Tear Finale (MapWorldsMemoryTear) ----------------------------------------------------------------------

    static async Task EnterMemoryTearFinale(Player player)
    {
        if (ClientHasQuestFlag(player, QfMemoryTearMapPortalsActive))
            await SetQuestFlags(player, [QfMemoryTearMapPortalsActive], on: false);
        await SetQuestFlags(player, [QfMapWorldsMemoryTearEntered, QfWitnessZana3]);

        if (player.Instance is not { } instance) return;
        var state = FinaleInstances.GetOrCreateValue(instance);
        if (state.EagonId == 0)
        {
            var (ex, ey) = player.Layout.Near((player.Layout.SpawnX + 14, player.Layout.SpawnY));
            var (zx, zy) = player.Layout.Near((ex + 6, ey));
            if (global::Game.Npcs.Find(EagonFragmentedMemoryPath) is { Supported: true } eagon)
            {
                var sm = (eagon.StateMachine ?? new StateMachineData([0], 0, ["disappear"])).Set("disappear", 0);
                state.EagonId = await global::Game.SpawnNpc(player, eagon with { StateMachine = sm }, ex, ey, 0f);
            }
            if (QuestObjectTypes.Find(ZanaFragmentedMemoryPath) is { } zanaInfo
                && QuestObject.CanSerialize(zanaInfo.Components, haveStateMachine: true))
            {
                uint zid = player.TakeObjectId();
                var zObj = new QuestObject(zx, zy, zanaInfo.Components, Rotation: MathF.PI)
                {
                    StateMachine = new StateMachineData([0], 0, ["disappear"]),
                };
                await global::Game.SendWorldCreate(player, zid,
                    () => InstanceEntities.CreateObject(zid, InstanceEntities.ObjectTypeHash(ZanaFragmentedMemoryPath), zObj.Blob()),
                    $"0x02bb create ZanaFragmentedMemory 0x{zid:x} at ({zx}, {zy})", wake: true);
                state.ZanaId = zid;
            }
        }
        // Let interacting with EagonFragmentedMemory or ZanaFragmentedMemory complete the finale;
        // if neither entity could spawn in this environment, complete immediately as fallback.
        if (state.EagonId == 0 && state.ZanaId == 0)
            await CompleteMemoryTearFinale(player, state);
    }

    static async Task CompleteMemoryTearFinale(Player player, FinaleInstanceState state)
    {
        if (state.Completed) return;
        state.Completed = true;
        await SetQuestFlags(player, [QfWitnessZana3_2]);
        var unlockLqfs = new[] { LqfAtlasDestructionHasBegun, LqfDestroyingAtlas1, LqfDestroyingAtlas2 }
            .Where(f => !player.Character.LeagueQuestFlags.Contains(f)).ToList();
        if (unlockLqfs.Count > 0)
            await SetLeagueQuestFlags(player, unlockLqfs);
        await PlayerOf(player).Tell("Zana's fragmented memory dissipates. Speak to Eagon in your Hideout to receive your first Astrolabe.");
    }

    // ---- Boss Kill & Memory Line Step Completion -----------------------------------------------------------------------

    /// <summary>Called when a map's final boss dies: advances any active Originator Memory Line step.</summary>
    public static async Task OnMapCompleted(Player player, AtlasProgress.Node node)
    {
        var run = MapDevice.ForInstance(player);
        if (run?.ContentData is null || !run.ContentData.TryGetValue("memory.lineId", out string? lineIdStr)
            || !uint.TryParse(lineIdStr, out uint lineId))
            return;

        var data = WorldOf(PlayerOf(player)).CharacterData;
        int? readyLqf = null;
        bool changed = Saves.Atomically(() =>
        {
            var lines = LoadLines(data);
            int idx = lines.FindIndex(l => l.LineId == lineId);
            if (idx < 0) return false;
            var line = lines[idx];
            if (line.CurrentStepIndex + 1 >= line.TotalSteps)
            {
                lines.RemoveAt(idx);
            }
            else
            {
                byte nextStep = (byte)(line.CurrentStepIndex + 1);
                lines[idx] = line with { CurrentStepIndex = nextStep };
                if (nextStep == line.TotalSteps - 1)
                {
                    readyLqf = line.FinalAreaId switch
                    {
                        CourtyardOfWastingArea => LqfCourtyardReady,
                        ChambersOfImpurityArea => LqfChambersReady,
                        TheatreOfLiesArea => LqfTheatreReady,
                        _ => null,
                    };
                }
            }
            SaveLines(data, lines);
            return true;
        });

        if (!changed) return;
        if (readyLqf is int lqf && !player.Character.LeagueQuestFlags.Contains(lqf))
            await SetLeagueQuestFlags(player, [lqf]);
        await SendMemoryLines(player);
    }

    static async Task OnMonsterKilled(Player player, MonsterInfo monster)
    {
        string vid = monster.VarietyId;
        var data = WorldOf(PlayerOf(player)).CharacterData;

        switch (vid)
        {
            case NeglectedFlameVariety:
                await CompleteMinibossChain(player, data, CourtyardOfWastingArea,
                    [LqfFirstMinibossDefeated, LqfMemorySiphoned1],
                    [LqfMemoryLineZana1Quest, LqfMemoryLineZana1QuestRepeat, LqfCourtyardReady]);
                break;

            case CardinalOfFearVariety:
                await CompleteMinibossChain(player, data, ChambersOfImpurityArea,
                    [LqfSecondMinibossDefeated, LqfMemorySiphoned2],
                    [LqfMemoryLineZana2Quest, LqfMemoryLineZana2QuestRepeat, LqfChambersReady]);
                break;

            case DeceitfulGodVariety:
                await CompleteMinibossChain(player, data, TheatreOfLiesArea,
                    [LqfThirdMinibossDefeated, LqfMemorySiphoned3],
                    [LqfMemoryLineZana3Quest, LqfMemoryLineZana3QuestRepeat, LqfTheatreReady]);
                break;

            case IncarnationOfNeglectVariety:
            case IncarnationOfNeglectUberVariety:
                if (!player.Character.LeagueQuestFlags.Contains(LqfNeglectDefeated))
                    await SetLeagueQuestFlags(player, [LqfNeglectDefeated]);
                await SetQuestFlags(player, [QfWitnessZana1]);
                break;

            case IncarnationOfFearVariety:
            case IncarnationOfFearUberVariety:
                if (!player.Character.LeagueQuestFlags.Contains(LqfFearDefeated))
                    await SetLeagueQuestFlags(player, [LqfFearDefeated]);
                await SetQuestFlags(player, [QfWitnessZana2]);
                break;

            case IncarnationOfDreadVariety:
            case IncarnationOfDreadUberVariety:
                var dreadLqfs = new[] { LqfDreadDefeated, LqfOriginatorVoidstoneUnlocked }
                    .Where(f => !player.Character.LeagueQuestFlags.Contains(f)).ToList();
                if (dreadLqfs.Count > 0) await SetLeagueQuestFlags(player, dreadLqfs);
                if (ClientHasQuestFlag(player, QfMemoryBossCollectBothRoses))
                    await SetQuestFlags(player, [QfMemoryBossCollectBothRoses], on: false);
                break;
        }
    }

    static async Task CompleteMinibossChain(Player player, ISavedData data, string finalAreaId, int[] setLqfs, int[] clearLqfs)
    {
        Saves.Atomically(() =>
        {
            var lines = LoadLines(data);
            if (lines.RemoveAll(l => string.Equals(l.FinalAreaId, finalAreaId, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveLines(data, lines);
        });
        var toSet = setLqfs.Where(f => !player.Character.LeagueQuestFlags.Contains(f)).ToList();
        if (toSet.Count > 0) await SetLeagueQuestFlags(player, toSet, on: true);
        var toClear = clearLqfs.Where(f => player.Character.LeagueQuestFlags.Contains(f)).ToList();
        if (toClear.Count > 0) await SetLeagueQuestFlags(player, toClear, on: false);
        await SendMemoryLines(player);
    }
}
