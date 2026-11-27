using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>
/// 3.28/3.29 Atlas Anomalies (<c>AtlasNode 166–181</c> ↔ <c>MapDeviceRecipes 74–89</c>): discovery/unlocking on map
/// completion (per-Anomaly LQFs <c>1836–1839, 1842–1853</c> + <c>LQF 1884</c> unlocking Kirac <c>NPCTalk 2325</c>),
/// selecting &amp; opening unlocked Anomaly nodes from the Map Device, consuming the Anomaly LQF on clear, and spawning
/// the encounter NPCs and objects (Cadiro, Ipaatiki Vaal Priestess, Nameless Seer, Ventor's Strongbox, Reflecting Mist,
/// Vaal Unique Sacrifice, Untainted Paradise, Overgrown Grove).
/// </summary>
public static class AtlasAnomalies
{
    /// <summary><c>LQF 1884 RandomContentAnomalyEncountered</c>: unlocks Kirac <c>NPCTalk 2325</c> ("Anomalies").</summary>
    public const int LqfAnomalyEncountered = 1884;

    public const string CadiroPath = "Metadata/NPC/Epilogue/CadiroVendorRandomContent";
    public const string VaalUniqueDealerPath = "Metadata/NPC/Epilogue/RandomAtlasContent/VaalUniqueDealer";
    public const string NamelessSeerPath = "Metadata/NPC/League/Azmeri/UniqueDealerMaps";
    public const string VentorsStrongboxPath = "Metadata/Chests/StrongBoxes/VentorsBaseBox";
    public const string ReflectingMistPath = "Metadata/Terrain/Leagues/Lake/Objects/CraftingObjectRandomAtlasContent";
    public const string VaalSacrificeDevicePath = "Metadata/Terrain/EndGame/MapAtlasEncounter_VaalUniqueSacrifice/Objects/VaalUniqueSacrificeDevice";

    public const string PerandusArea = "MapAtlasEncounter_Perandus";
    public const string NamelessSeerArea = "MapAtlasEncounter_NamelessSeer";
    public const string ReflectingMistArea = "MapAtlasEncounter_ReflectingMist";
    public const string VaalSacrificeArea = "MapAtlasEncounter_VaalUniqueSacrifice";
    public const string VentorsStrongboxArea = "MapAtlasEncounter_VentorsStrongbox";
    public const string VaalTraderArea = "MapAtlasEncounter_VaalTrader";
    public const string UntaintedParadiseArea = "MapWorldsTropicalIslandUnique";
    public const string OvergrownGroveArea = "HarvestLeagueMemoryLine";

    const string SelectedTargetKey = "atlas.selectedTarget";

    public sealed record AnomalyDef(
        int NodeRow, string Id, ushort NodeHash16, int Region, int RecipeRow,
        int WorldAreaRow, string AreaId, ushort AreaHash16, string AreaName,
        int AreaLevel, int UnlockLqf, IReadOnlyList<int> ModRows);

    static List<AnomalyDef> _anomalies = [];
    static Dictionary<ushort, AnomalyDef> _byNodeHash = [];
    static Dictionary<int, AnomalyDef> _byLqf = [];
    static Dictionary<string, List<AnomalyDef>> _byAreaId = new(StringComparer.OrdinalIgnoreCase);

    sealed class EncounterState
    {
        public bool Spawned;
        public bool Cleared;
        public int AnomalyLqf;
        public string AnomalyId = "";
        public uint InteractiveObjectId;
        public int ObjX, ObjY;
        public string ObjPath = "";
    }

    static readonly Store<InstanceState, EncounterState> Instances = new();
    static readonly string[] FlagMapOwner = [MemoryTears.QfIsMapOwner];
    static readonly string[] FlagNone = [];

    public static IReadOnlyList<AnomalyDef> All => _anomalies;

    public static AnomalyDef? FindByNodeHash16(ushort hash16) => _byNodeHash.GetValueOrDefault(hash16);
    public static AnomalyDef? FindByLqf(int lqf) => _byLqf.GetValueOrDefault(lqf);

    public static void LoadData()
    {
        var list = new List<AnomalyDef>();
        var byNodeHash = new Dictionary<ushort, AnomalyDef>();
        var byLqf = new Dictionary<int, AnomalyDef>();
        var byArea = new Dictionary<string, List<AnomalyDef>>(StringComparer.OrdinalIgnoreCase);

        if (!ClientTables.Has("AtlasNode") || !ClientTables.Has("MapDeviceRecipes") || !ClientTables.Has("WorldAreas"))
        {
            _anomalies = list; _byNodeHash = byNodeHash; _byLqf = byLqf; _byAreaId = byArea;
            return;
        }

        var atlasRows = ClientTables.Table("AtlasNode").ToList();
        var areaRows = ClientTables.Table("WorldAreas").ToList();
        var recipesByNode = new Dictionary<int, (int Row, int AreaRow, int AreaLevel, int Lqf, int[] Mods)>();
        int rIdx = 0;
        foreach (var r in ClientTables.Table("MapDeviceRecipes"))
        {
            if (r.IntOrNull("AtlasNode") is int nodeRow && nodeRow >= 166)
            {
                recipesByNode[nodeRow] = (
                    rIdx,
                    r.IntOrNull("WorldArea") ?? 0,
                    r.IntOrNull("AreaLevel") ?? 0,
                    r.IntOrNull("__col12") ?? 0,
                    [.. r.Ints("Mods")]);
            }
            rIdx++;
        }

        for (int nRow = 166; nRow < atlasRows.Count; nRow++)
        {
            var n = atlasRows[nRow];
            int lqf = n.IntOrNull("__col37") ?? 0;
            if (lqf <= 0 || lqf >= 1911) continue;
            string id = n.Text("Id") ?? $"Anomaly{nRow}";
            ushort nodeHash = (ushort)(n.IntOrNull("HASH16") ?? 0);
            int region = n.IntOrNull("Region") ?? 0;
            int areaRow = n.IntOrNull("Area2") ?? n.IntOrNull("Area1") ?? -1;
            int recipeRow = -1, areaLevel = 83;
            int[] modRows = [];
            if (recipesByNode.TryGetValue(nRow, out var rec))
            {
                recipeRow = rec.Row;
                if (rec.AreaRow > 0) areaRow = rec.AreaRow;
                if (rec.AreaLevel > 0) areaLevel = rec.AreaLevel;
                if (rec.Lqf > 0 && rec.Lqf < 1911) lqf = rec.Lqf;
                modRows = rec.Mods;
            }
            if (areaRow < 0 || areaRow >= areaRows.Count) continue;
            var wa = areaRows[areaRow];
            string areaId = wa.Text("Id") ?? "";
            ushort areaHash = (ushort)(wa.IntOrNull("HASH16") ?? 0);
            string areaName = wa.Text("Name") ?? id;
            int resolvedLevel = areaLevel > 0 ? areaLevel : (wa.IntOrNull("AreaLevel") is int al and > 0 ? al : 83);
            var def = new AnomalyDef(nRow, id, nodeHash, region, recipeRow, areaRow, areaId, areaHash, areaName, resolvedLevel, lqf, modRows);
            list.Add(def);
            if (nodeHash != 0) byNodeHash[nodeHash] = def;
            byLqf[lqf] = def;
            if (!byArea.TryGetValue(areaId, out var areaList)) byArea[areaId] = areaList = [];
            areaList.Add(def);
        }

        _anomalies = list;
        _byNodeHash = byNodeHash;
        _byLqf = byLqf;
        _byAreaId = byArea;
    }

    public static void Register(string owner)
    {
        GameEvents.AreaQuestFlags.Add(owner, TransientAnomalyFlags);
        GameEvents.MapOpening.Add(owner, OnMapOpening);
        GameEvents.PlayerEntered.Add(owner, OnPlayerEntered);
        GameEvents.ObjectUse.Add(owner, OnObjectUse);
        GameEvents.NpcOption.Add(owner, OnNpcOption);
        GameEvents.MonsterKilled.Add(owner, kill => OnMonsterKilled(kill.Player));
        GameEvents.Kill.Add(owner, kill =>
        {
            if (kill.PlayerKill) return;
            if (!_byAreaId.ContainsKey(kill.Player.Layout.Id)) return;
            kill.Later(() => OnMonsterKilled(kill.Player));
        });
    }

    static IReadOnlyList<string> TransientAnomalyFlags(Player player) =>
        _byAreaId.ContainsKey(player.Layout.Id) ? FlagMapOwner : FlagNone;

    // ---- Discovery / Unlocking on Map Completion -----------------------------------------------------------------------

    /// <summary>Rolls Atlas Anomaly discovery when a normal Atlas map is completed.
    /// Sets the discovered Anomaly's LQF (<c>1836–1839, 1842–1853</c>) and <c>LQF 1884</c> (<c>RandomContentAnomalyEncountered</c>).</summary>
    public static async Task<AnomalyDef?> TryDiscoverOnMapCompletion(Player player, Random? rng = null, bool guaranteed = false)
    {
        if (_anomalies.Count == 0) return null;
        var random = rng ?? Random.Shared;
        if (!guaranteed && random.Next(100) >= 15) return null;

        var lqfs = player.Character.LeagueQuestFlags;
        var eligible = _anomalies.Where(a => !lqfs.Contains(a.UnlockLqf)).ToList();
        if (eligible.Count == 0) return null;

        var chosen = eligible[random.Next(eligible.Count)];
        await UnlockAnomaly(player, chosen);
        return chosen;
    }

    public static async Task UnlockAnomaly(Player player, AnomalyDef anomaly)
    {
        var toSet = new List<int> { anomaly.UnlockLqf };
        if (!player.Character.LeagueQuestFlags.Contains(LqfAnomalyEncountered))
            toSet.Add(LqfAnomalyEncountered);
        var missing = toSet.Where(f => !player.Character.LeagueQuestFlags.Contains(f)).ToList();
        if (missing.Count > 0) await SetLeagueQuestFlags(player, missing, on: true);
        await PlayerOf(player).Tell($"Atlas Anomaly discovered: {anomaly.AreaName} ({anomaly.Id}) is now open on your Atlas.");
    }

    /// <summary>Clears/consumes an unlocked Anomaly's LQF when its encounter is cleared.</summary>
    public static async Task<bool> ConsumeAnomaly(Player player, int anomalyLqf)
    {
        if (anomalyLqf <= 0 || !player.Character.LeagueQuestFlags.Contains(anomalyLqf)) return false;
        await SetLeagueQuestFlags(player, [anomalyLqf], on: false);
        return true;
    }

    // ---- Selecting & Opening Unlocked Anomaly Nodes via Map Device -----------------------------------------------------

    static AnomalyDef? ResolveSelectedAnomaly(Player player, ushort requestTarget)
    {
        var lqfs = player.Character.LeagueQuestFlags;
        AnomalyDef? MatchHash(ushort h)
        {
            if (h == 0) return null;
            if (_byNodeHash.TryGetValue(h, out var byNode) && lqfs.Contains(byNode.UnlockLqf))
                return byNode;
            return _anomalies.FirstOrDefault(a => a.AreaHash16 == h && lqfs.Contains(a.UnlockLqf));
        }
        if (MatchHash(requestTarget) is { } direct) return direct;
        string? saved = CharacterSaves.ModuleData(CharacterName(player), SelectedTargetKey);
        if (ushort.TryParse(saved, out ushort savedHash))
            return MatchHash(savedHash);
        return null;
    }

    static void OnMapOpening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not null) return;
        // Anomalies open without placing a map item (MapDeviceRecipes 74..89 use dummy item 3920 and are LQF-gated).
        if (request.Slots.Any(s => s.IsMap)) return;

        var anomaly = ResolveSelectedAnomaly(request.Player, request.Target);
        if (anomaly is null) return;

        var mods = BuildRecipeMods(anomaly.ModRows);
        var area = new MapDevice.MapArea(anomaly.AreaHash16, anomaly.AreaId, anomaly.AreaName, 16)
        {
            Level = anomaly.AreaLevel,
            UsesAtlas = true,
        };
        request.Opening = new MapDevice.Opening(area, mods)
        {
            ContentData = new Dictionary<string, string>
            {
                ["atlas.anomalyLqf"] = anomaly.UnlockLqf.ToString(),
                ["atlas.anomalyId"] = anomaly.Id,
            },
        };
        request.ConsumeSlots = [];
        request.Reason = "";
        if (CharacterName(request.Player) is { } character)
            CharacterSaves.ChangeModuleData(character, SelectedTargetKey, _ => "");
    }

    static MapMods BuildRecipeMods(IReadOnlyList<int> modRows)
    {
        if (modRows.Count == 0) return MapMods.None;
        var sum = new Dictionary<uint, int>();
        foreach (int row in modRows)
            if (Items.Mods.Row(row) is { } mod)
                foreach (var stat in mod.Stats)
                    if (stat.Min != 0) sum[stat.Id] = sum.GetValueOrDefault(stat.Id) + stat.Min;
        return sum.Count == 0 ? MapMods.None : new MapMods(sum) { Implicit = sum };
    }

    // ---- Anomaly Encounter Spawns & Interactions -----------------------------------------------------------------------

    static int ResolveInstanceAnomalyLqf(Player player)
    {
        var run = MapDevice.ForInstance(player);
        if (run?.ContentData is { } cd && cd.TryGetValue("atlas.anomalyLqf", out string? raw) && int.TryParse(raw, out int lqf))
            return lqf;
        if (_byAreaId.TryGetValue(player.Layout.Id, out var candidates))
        {
            var active = candidates.FirstOrDefault(a => player.Character.LeagueQuestFlags.Contains(a.UnlockLqf));
            if (active is not null) return active.UnlockLqf;
        }
        return 0;
    }

    static async Task OnPlayerEntered(Player player)
    {
        if (!_byAreaId.ContainsKey(player.Layout.Id) || player.Instance is not { } instance) return;
        var state = Instances.GetOrCreateValue(instance);
        if (state.Spawned) return;
        state.Spawned = true;
        state.AnomalyLqf = ResolveInstanceAnomalyLqf(player);
        state.AnomalyId = _byLqf.GetValueOrDefault(state.AnomalyLqf)?.Id ?? "";

        var (ex, ey) = player.Layout.Near((player.Layout.SpawnX + 18, player.Layout.SpawnY));
        switch (player.Layout.Id)
        {
            case PerandusArea:
                if (global::Game.Npcs.Find(CadiroPath) is { Supported: true } cadiro)
                    await global::Game.SpawnNpc(player, cadiro, ex, ey, 0f);
                break;

            case VaalTraderArea:
                if (global::Game.Npcs.Find(VaalUniqueDealerPath) is { Supported: true } vaalDealer)
                {
                    var sm = (vaalDealer.StateMachine ?? new StateMachineData([0], 0, ["seen"])).Set("seen", 0);
                    await global::Game.SpawnNpc(player, vaalDealer with { StateMachine = sm }, ex, ey, 0f);
                }
                break;

            case NamelessSeerArea:
                if (global::Game.Npcs.Find(NamelessSeerPath) is { Supported: true } seer)
                {
                    var sm = (seer.StateMachine ?? new StateMachineData([0, 0], 0, ["seen", "spawned"]))
                        .Set("seen", 0).Set("spawned", 1);
                    await global::Game.SpawnNpc(player, seer with { StateMachine = sm }, ex, ey, 0f);
                }
                break;

            case VentorsStrongboxArea:
                await SpawnEncounterObject(player, state, VentorsStrongboxPath, ex, ey,
                    new StateMachineData([0, 0], 0, ["dropping_item", "complete"]));
                break;

            case ReflectingMistArea:
                await SpawnEncounterObject(player, state, ReflectingMistPath, ex, ey,
                    new StateMachineData([0], 0, ["created"]));
                break;

            case VaalSacrificeArea:
                await SpawnEncounterObject(player, state, VaalSacrificeDevicePath, ex, ey,
                    new StateMachineData([0], 0, ["interacted"]));
                break;
        }
    }

    static async Task SpawnEncounterObject(Player player, EncounterState state, string path, int x, int y, StateMachineData sm)
    {
        var info = QuestObjectTypes.Find(path);
        if (info is null || !QuestObject.CanSerialize(info.Components, haveStateMachine: true)) return;
        uint id = player.TakeObjectId();
        var obj = new QuestObject(x, y, info.Components, Rotation: 0f)
        {
            StateMachine = sm,
            Model = info.AnimatedModel,
        };
        await global::Game.SendWorldCreate(player, id,
            () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(path), obj.Blob()),
            $"0x02bb create Anomaly object {path.Split('/')[^1]} 0x{id:x} at ({x}, {y})", wake: true);
        state.InteractiveObjectId = id;
        state.ObjX = x;
        state.ObjY = y;
        state.ObjPath = path;
    }

    static async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || !Instances.TryGetValue(instance, out var state)) return false;
        if (state.InteractiveObjectId == 0 || use.TargetId != state.InteractiveObjectId || state.Cleared) return false;

        if (!TryBeginObjectWalk(player, requestedAt, state.ObjX, state.ObjY, 4, out var walk)) return true;
        var (action, now, _, _, arrive) = walk;
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action) || state.Cleared) return true;
        state.Cleared = true;

        if (state.ObjPath == VaalSacrificeDevicePath && QuestObjectTypes.Find(VaalSacrificeDevicePath) is { } sacInfo)
        {
            int smIdx = sacInfo.Components.ToList().IndexOf("StateMachine");
            if (smIdx >= 0)
                await global::Game.SendWorldRecord(player, state.InteractiveObjectId,
                    InstanceEntities.StateMachineUpdate(state.InteractiveObjectId, (byte)smIdx, new StateMachineData([1], 0, ["interacted"])),
                    $"0x02bc VaalUniqueSacrificeDevice 0x{state.InteractiveObjectId:x} interacted=1");
            await DropAnomalyReward(player, state.ObjX, state.ObjY, corrupted: true, mirrored: false, count: 1);
        }
        else if (state.ObjPath == ReflectingMistPath)
        {
            await DropAnomalyReward(player, state.ObjX, state.ObjY, corrupted: false, mirrored: true, count: 1);
        }
        else if (state.ObjPath == VentorsStrongboxPath)
        {
            if (QuestObjectTypes.Find(VentorsStrongboxPath) is { } boxInfo)
            {
                int smIdx = boxInfo.Components.ToList().IndexOf("StateMachine");
                if (smIdx >= 0)
                    await global::Game.SendWorldRecord(player, state.InteractiveObjectId,
                        InstanceEntities.StateMachineUpdate(state.InteractiveObjectId, (byte)smIdx, new StateMachineData([1, 1], 0, ["dropping_item", "complete"])),
                        $"0x02bc VentorsStrongbox 0x{state.InteractiveObjectId:x} opened");
            }
            await DropAnomalyReward(player, state.ObjX, state.ObjY, corrupted: false, mirrored: false, count: 3);
        }

        await ClearInstanceAnomaly(player, state);
        return true;
    }

    static async Task<bool> OnNpcOption(NpcOption option)
    {
        var player = option.Player;
        if (player.Instance is not { } instance || !Instances.TryGetValue(instance, out var state)) return false;

        if (string.Equals(option.Category, "RandomContentPerandus", StringComparison.OrdinalIgnoreCase)
            || option.Row == 3784)
        {
            if (!state.Cleared)
            {
                state.Cleared = true;
                var (px, py) = player.Walk.Position(AreaTime(player));
                await DropAnomalyReward(player, px, py, corrupted: false, mirrored: false, count: 1);
                await ClearInstanceAnomaly(player, state);
                await PlayerOf(player).Tell("Cadiro Perandus presents a rare Perandus deal.");
            }
            return true;
        }

        if (string.Equals(option.Category, "ChooseOneUnique", StringComparison.OrdinalIgnoreCase)
            || option.Row is 3526 or 3529 or 3530)
        {
            if (!state.Cleared)
            {
                state.Cleared = true;
                bool corrupted = player.Layout.Id == VaalTraderArea;
                var (px, py) = player.Walk.Position(AreaTime(player));
                await DropAnomalyReward(player, px, py, corrupted: corrupted, mirrored: false, count: 1);
                await ClearInstanceAnomaly(player, state);
                await PlayerOf(player).Tell(corrupted
                    ? "Ipaatiki grants you a corrupted Vaal gift."
                    : "The Nameless Seer grants you a gift.");
            }
            return true;
        }

        return false;
    }

    static async Task OnMonsterKilled(Player player)
    {
        if (player.Instance is not { } instance || !Instances.TryGetValue(instance, out var state) || state.Cleared)
            return;
        // Combat-cleared anomalies (Untainted Paradise, Overgrown Grove, Betrayal Safehouses, Blueprint Wing, Logbook):
        // consume the Anomaly LQF once no hostile monsters remain alive in the area.
        if (player.Layout.Id is PerandusArea or VaalTraderArea or NamelessSeerArea
            or VentorsStrongboxArea or ReflectingMistArea or VaalSacrificeArea)
            return;
        bool anyHostileAlive = player.Area.Monsters.Values.Concat(player.Area.DormantMonsters.Values)
            .Any(m => m.Hostile && !m.Dead && m.Life > 0);
        if (anyHostileAlive) return;
        state.Cleared = true;
        await ClearInstanceAnomaly(player, state);
    }

    static async Task ClearInstanceAnomaly(Player player, EncounterState state)
    {
        if (state.AnomalyLqf <= 0) return;
        await ConsumeAnomaly(player, state.AnomalyLqf);
    }

    static async Task DropAnomalyReward(Player player, int x, int y, bool corrupted, bool mirrored, int count)
    {
        var rng = global::Game.AreaRng(player);
        var catalog = ItemsFor(player);
        string[] pool = mirrored
            ? ["Metadata/Items/Rings/Ring5", "Metadata/Items/Amulets/Amulet4"]
            : ["Metadata/Items/Currency/CurrencyRerollRare", "Metadata/Items/Currency/CurrencyAddModToRare", "Metadata/Items/Currency/CurrencyCorrupt"];
        for (int i = 0; i < count; i++)
        {
            var type = catalog.Find(pool[rng.Next(pool.Length)])
                ?? catalog.All.FirstOrDefault(t => t.Supported && t.Class == "StackableCurrency");
            if (type is null) continue;
            var item = ItemCatalog.Create(type) with { ItemLevel = 83, Corrupted = corrupted, Mirrored = mirrored };
            if (mirrored) item = RollsFor(player).Roll(item, ItemRarity.Rare, rng) with { Mirrored = true };
            var at = player.Layout.Near((x + i * 2, y));
            var ground = new WorldItemObject(at.X, at.Y, item);
            uint id = player.Instance.Ground.Add(ground, player);
            await global::Game.SendGroundItem(player, id, ground, $"Atlas Anomaly reward ({player.Layout.Id})", justDropped: true);
        }
    }
}
