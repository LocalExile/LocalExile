using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Forge of the Titans (<c>CrucibleLeagueArea</c>, <c>CrucibleLeagueAreaUber</c>):
/// - Sends S→C <c>0x1f</c> <c>CrucibleMapRules</c> (<c>0x8c</c>), which is only the area's music (<c>0x141682250</c>).
/// - Puts a <c>CrucibleEndgameMonsterPacks</c> pack at the <c>CrucibleEndgameMonstersSpawnerBase</c> spots.
/// - Places <c>CrucibleCombiningBench</c> (from <c>arena_01.tmd</c>), which opens the client's combining window
///   (<c>0x7b</c> <c>0x4a</c>) and is used up by one combine (C→S <c>0x299</c> → S→C <c>0x29a</c>).
/// </summary>
public static class CrucibleForgeOfTheTitans
{
    sealed class WorldObject
    {
        public uint Id;
        public string Type = "";
        public int X, Y;
        public float Rotation;
        public byte TransitionState = 1;
        public Dictionary<string, int> States = [];
    }

    sealed class ForgeAreaState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Built;
        public bool Uber;
        public int Level;
        public CrucibleTreeEngine.CombineModifiers CombineMods = CrucibleTreeEngine.CombineModifiers.From(MapMods.None);
        public uint CombiningBenchId;
        public bool CombiningUsed;
        public List<WorldObject> Objects = [];
        public bool PacksSpawned;
        /// <summary>Who the bench opened its window for: only they can combine.</summary>
        public HashSet<ClientState> Opened = [];
    }

    static readonly ConditionalWeakTable<InstanceState, ForgeAreaState> Areas = new();
    static readonly ConditionalWeakTable<ClientState, HashSet<uint>> Presented = new();

    // Our own number, GGG never published it: how many of the area's 45 spawner spots stay empty.
    const int EmptySpawnerPercent = 30;

    public static void Clear()
    {
        Areas.Clear();
        Presented.Clear();
    }

    /// <summary>Populates Forge of the Titans on <see cref="GameEvents.AreaGenerated"/>.</summary>
    public static async Task Enter(ClientState conn)
    {
        if (!CrucibleTables.IsForgeArea(conn.Layout.Id) || conn.Instance is null || conn.Layout.Level is not { } level)
            return;

        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceProtocol.AddRuleset(InstanceCrucible.CrucibleMapRulesetRow),
            "0x001f ruleset CrucibleMapRules");

        var area = Areas.GetOrCreateValue(conn.Instance);
        bool spawn;
        await area.Gate.WaitAsync();
        try
        {
            if (!area.Built)
            {
                Build(conn, area, level);
                area.Built = true;
            }
            // Under the gate: two players arriving together spawn the packs once.
            spawn = !area.PacksSpawned;
            area.PacksSpawned = true;
        }
        finally { area.Gate.Release(); }

        foreach (var obj in area.Objects)
            await Present(conn, obj);

        if (spawn)
            await SpawnEndgamePacks(conn, area, level);
    }

    static void Build(ClientState conn, ForgeAreaState area, ServerLevels.Level level)
    {
        area.Uber = CrucibleTables.IsUberForgeArea(conn.Layout.Id);
        var mapRun = MapRunOf(conn);
        area.Level = mapRun?.Area.Level is > 0 ? mapRun.Area.Level : ServerLevels.AreaLevel(conn.Layout.Id);
        area.CombineMods = CrucibleTreeEngine.CombineModifiers.From(mapRun?.Mods ?? MapMods.None);

        // CrucibleCombiningBench from arena_01.tmd (TileQuestObjects). The 3.29 client creates it, though no table of
        // its object register names it (seen live on 3.29.3d).
        if (level.TileQuestObjects.FirstOrDefault(o =>
            o.Type.Equals(CrucibleTables.CombiningBenchType, StringComparison.OrdinalIgnoreCase)) is { } bp)
        {
            var bench = new WorldObject
            {
                Id = conn.Instance.World.NewMonsterId(),
                Type = CrucibleTables.CombiningBenchType,
                X = bp.X,
                Y = bp.Y,
                Rotation = bp.Rotation,
                TransitionState = 1,
            };
            area.CombiningBenchId = bench.Id;
            area.Objects.Add(bench);
        }

        // CrucibleEndgameMonstersSpawnerBase pool visuals along the walkway.
        foreach (var spawner in level.RoomObjects.Where(o =>
            o.Type.Equals(CrucibleTables.SpawnerBaseType, StringComparison.OrdinalIgnoreCase)))
        {
            area.Objects.Add(new WorldObject
            {
                Id = conn.Instance.World.NewMonsterId(),
                Type = CrucibleTables.SpawnerBaseType,
                X = spawner.X,
                Y = spawner.Y,
                Rotation = spawner.Rotation,
                TransitionState = 1,
                States = new() { ["pool"] = 1 },
            });
        }

        Console.WriteLine($"[crucible] Forge of the Titans ({conn.Layout.Id}, lvl {area.Level}, uber={area.Uber}): "
            + $"bench=0x{area.CombiningBenchId:x}"
            + $", spawners={area.Objects.Count(o => o.Type == CrucibleTables.SpawnerBaseType)}");
    }

    static async Task SpawnEndgamePacks(ClientState conn, ForgeAreaState area, ServerLevels.Level level)
    {
        var spawners = level.RoomObjects
            .Where(o => o.Type.Equals(CrucibleTables.SpawnerBaseType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (spawners.Count == 0) return;

        var rng = new Random(unchecked((int)(conn.Seed ^ 0x43525543u)));
        var tables = CrucibleTables.Current;
        var scaling = CrucibleMonsters.ScalingMods(area.Level);
        byte monsterLevel = (byte)Math.Clamp(area.Level, 1, 100);

        var (remote, stream, ciphers) = conn.Wire;
        int totalSpawned = 0;

        foreach (var sp in spawners)
        {
            if (conn.Closed || conn.LeavingArea) break;
            if (rng.Next(100) < EmptySpawnerPercent) continue;

            // Ours: a random theme per spawner.
            var theme = CrucibleMonsters.ThemeFor(rng.Next(Math.Max(1, CrucibleMonsters.Pools.Count)), rng);
            var pack = CrucibleMonsters.RollEndgamePack(tables, theme, rng);

            for (int k = 0; k < pack.Count; k++)
            {
                if (conn.Closed || conn.LeavingArea) break;
                var (variety, rarity) = pack[k];
                var cell = conn.Layout.Near((sp.X + (k % 3 - 1) * 4, sp.Y + (k / 3 - 1) * 4));
                uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, monsterLevel, rarity,
                    cell.X, cell.Y, unrisen: false, alone: false, extraMods: scaling);
                if (id != 0) totalSpawned++;
            }
        }

        Console.WriteLine($"[crucible] Forge of the Titans: spawned {totalSpawned} Crucible monsters across {spawners.Count} spawners");
    }

    static (QuestObject Object, CrucibleTables.ObjectTypeInfo Info)? Describe(WorldObject obj)
    {
        if (!CrucibleTables.Current.Objects.TryGetValue(obj.Type, out var info)) return null;
        var states = info.States;
        if (states is not null)
        {
            foreach (var (k, v) in obj.States)
                states = states.Set(k, v);
        }
        var quest = new QuestObject(obj.X, obj.Y, info.Components, obj.Rotation, TransitionState: obj.TransitionState)
        {
            StateMachine = states,
        };
        return QuestObject.CanSerialize(info.Components, states is not null) ? (quest, info) : null;
    }

    static async Task Present(ClientState conn, WorldObject obj)
    {
        var shown = Presented.GetOrCreateValue(conn);
        if (shown.Contains(obj.Id) || Describe(obj) is not var (quest, info)) return;
        byte[] blob = InstanceEntities.CreateObject(obj.Id, InstanceEntities.ObjectTypeHash(obj.Type), quest.Blob());
        await SendWorldCreate(conn, obj.Id, () => blob, $"crucible {obj.Type.Split('/')[^1]} 0x{obj.Id:x} at ({obj.X},{obj.Y})",
            wake: true, awaitBirth: false);
        if (info.Blocking)
        {
            if (info.HasStationaryCollision)
            {
                var (size, hull) = StationaryCollision.Resolve(obj.Type);
                if (size >= 0) conn.SetBlocker(obj.Id, obj.X, obj.Y, size, hull);
            }
            else
            {
                conn.SetBlocker(obj.Id, obj.X, obj.Y, Math.Max(0, info.Size - 1), null);
            }
        }
        shown.Add(obj.Id);
    }

    static async Task TransitionUsed(ClientState conn, WorldObject obj)
    {
        obj.TransitionState = 2;
        if (!CrucibleTables.Current.Objects.TryGetValue(obj.Type, out var info)) return;
        int trIndex = QuestObject.TransitionableIndex(info.Components);
        if (trIndex < 0) return;

        foreach (var member in CrucibleEncounter.Members(conn))
        {
            if (!Presented.TryGetValue(member, out var shown) || !shown.Contains(obj.Id)) continue;
            var (remote, stream, ciphers) = member.Wire;
            await SendWorldRecord(remote, stream, ciphers, member, obj.Id,
                InstanceEntities.TransitionableUpdate(obj.Id, (byte)trIndex, 2),
                $"0x02bc crucible {obj.Type.Split('/')[^1]} transitioned (used)");
        }
    }

    /// <summary>A click on the unused <c>CrucibleCombiningBench</c> opens the combining window.</summary>
    public static async Task<bool> UseObject(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!CrucibleTables.IsForgeArea(conn.Layout.Id) || conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area))
            return false;

        if (use.TargetId != area.CombiningBenchId || area.CombiningUsed
            || area.Objects.FirstOrDefault(o => o.Id == use.TargetId) is not { } bench)
            return false;
        if (!await WorldOf(conn).WalkUpTo(bench.X, bench.Y, 16, requestedAt))
            return true;

        lock (area.Opened) area.Opened.Add(conn);
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceCrucible.OpenCombiningUi(area.Uber),
            $"0x007b open CrucibleCombiningWindow (uber={area.Uber})");
        return true;
    }

    /// <summary>Handles C→S <c>0x0299</c>: combine the top item's tree into the bottom item's at the bench. The window
    /// enables Forge only for a pair it accepts, so a refused request gets a log line and no answer.</summary>
    public static async Task<bool> HandleCombine(ClientState conn, byte[] bytes)
    {
        var req = InstanceCrucible.ReadCombineRequest(bytes);
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area) || area.CombiningUsed)
            return true;
        // Only from the window the bench opened: a request sent from the entrance combines nothing.
        lock (area.Opened)
            if (!area.Opened.Contains(conn)) return true;

        var items = ItemsOf(conn);
        var catalog = ItemsFor(conn);
        (byte Row, InventoryItem Item, ItemCatalog.BaseType Type)? Find(uint inventoryId, uint itemId) =>
            PlayerInventories.Row(inventoryId) is byte row && row != PlayerInventories.Cursor
            && items.Items(row).FirstOrDefault(i => i.Id == itemId) is { } item
            && catalog.ByHash(item.Item.TypeHash) is { } type ? (row, item, type) : null;
        if (Find(req.TopInventoryId, req.TopItemId) is not var (topRow, topItem, topType)
            || Find(req.BottomInventoryId, req.BottomItemId) is not var (botRow, botItem, botType))
        {
            Console.WriteLine($"[crucible] {CharacterName(conn)} combine refused: no item at inventory {req.TopInventoryId} id {req.TopItemId}"
                + $" or inventory {req.BottomInventoryId} id {req.BottomItemId}");
            return true;
        }

        // ClientStrings 6790 "Inputs cannot be the same item." (0x1424bb850).
        int refusal = topItem.Id == botItem.Id ? 6790
            : CrucibleTreeEngine.CombineRefusal(topItem.Item, topType, botItem.Item, botType, catalog.Mods, area.Uber);
        if (refusal != 0)
        {
            Console.WriteLine($"[crucible] {CharacterName(conn)} combine of {topType.Name} into {botType.Name} refused: ClientStrings {refusal}");
            return true;
        }

        InventoryItem? updatedBottom;
        IReadOnlyList<InstanceCrucible.NodeMutation> mutations;
        await area.Gate.WaitAsync();
        try
        {
            if (area.CombiningUsed) return true;
            (var combined, mutations) = CrucibleTreeEngine.CombineTrees(
                topItem.Item, botItem.Item, botType, catalog.Mods, area.CombineMods, Random.Shared);
            // The throw takes the removal back out of the save and closes the connection (Saves).
            updatedBottom = Saves.Atomically(() => items.Remove(topRow, topItem.Id) is null ? null
                : items.Change(botRow, botItem.Id, _ => combined)
                    ?? throw new InvalidOperationException("Crucible combine: the surviving item is gone"));
            if (updatedBottom is null) return true;
            area.CombiningUsed = true;
        }
        finally { area.Gate.Release(); }

        var (remote, stream, ciphers) = conn.Wire;
        uint topInventory = PlayerInventories.InventoryId(topRow), bottomInventory = PlayerInventories.InventoryId(botRow);
        if (topRow == botRow)
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(bottomInventory, items.NextRevision(botRow), [topItem.Id, botItem.Id], [updatedBottom]),
                $"0x00bf inventory {bottomInventory}: Crucible combine destroyed {topItem.Id}, updated {updatedBottom.Id}");
        else
        {
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(topInventory, items.NextRevision(topRow), [topItem.Id], []),
                $"0x00bf inventory {topInventory}: Crucible combine destroyed {topItem.Id}");
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(bottomInventory, items.NextRevision(botRow), [botItem.Id], [updatedBottom]),
                $"0x00bf inventory {bottomInventory}: Crucible combine updated {updatedBottom.Id}");
        }

        await Send(remote, stream, ciphers, InstanceCrucible.CombineResult(bottomInventory, updatedBottom.Id, mutations),
            $"0x029a Crucible combine result inv={bottomInventory} item={updatedBottom.Id} "
            + $"flags=[{string.Join(" ", mutations.Select(m => $"({m.Column},{m.Row})={(byte)m.Flags}"))}]");

        if (area.Objects.FirstOrDefault(o => o.Id == area.CombiningBenchId) is { } benchObj)
            await TransitionUsed(conn, benchObj);

        if (CrucibleItemActions.IsEquipmentRow(topRow) || CrucibleItemActions.IsEquipmentRow(botRow))
            await WorldOf(conn).RefreshStats("Crucible combine");
        // An equipped top item leaves the hands.
        if (CrucibleItemActions.IsEquipmentRow(topRow))
            await SendModelIfHandsChanged(remote, stream, ciphers, conn);

        Console.WriteLine($"[crucible] {CharacterName(conn)} combined {topType.Name} into {botType.Name} ({mutations.Count} changed skill(s))");
        return true;
    }
}
