using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Forge of the Titans endgame area (<c>CrucibleLeagueArea</c> ilvl 80, <c>CrucibleLeagueAreaUber</c> ilvl 84):
/// - Sends S→C <c>0x1f</c> <c>CrucibleMapRules</c> (<c>0x8c</c>).
/// - Populates <c>CrucibleEndgameMonstersSpawnerBase</c> spawners with 8-tier <c>CrucibleEndgameMonsterPacks</c>.
/// - Places <c>CrucibleCombiningBench</c> (from <c>arena_01.tmd</c>) and optional <c>CrucibleUniqueForge</c>
///   (Antique Furnace / Scouring Furnace when rolled on the Remnant).
/// - Handles <c>0x7b</c> <c>0x4a</c>/<c>0x4b</c>, C→S <c>0x299</c> → S→C <c>0x29a</c>, and C→S <c>0x29b</c> → S→C <c>0x29c</c>.
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
        public MapMods RemnantMods = MapMods.None;
        public CrucibleTreeEngine.CombineModifiers CombineMods = CrucibleTreeEngine.CombineModifiers.From(MapMods.None);
        public uint CombiningBenchId;
        public bool CombiningUsed;
        public uint UniqueForgeId;
        public uint UniqueForgeMode;
        public bool UniqueForgeUsed;
        public List<WorldObject> Objects = [];
        public HashSet<uint> SpawnedMonsters = [];
    }

    sealed class CombineRollbackException : Exception;

    sealed record DropBuckets(
        IReadOnlyList<ItemCatalog.BaseType> Melee,
        IReadOnlyList<ItemCatalog.BaseType> Ranged,
        IReadOnlyList<ItemCatalog.BaseType> Shields);

    static readonly HashSet<int> MeleeClassRows = [6, 7, 9, 10, 11, 12, 14, 15, 16, 17, 32, 56, 57];
    static readonly HashSet<int> RangedClassRows = [8, 13];
    const int ShieldClassRow = 26;

    static readonly ConditionalWeakTable<InstanceState, ForgeAreaState> Areas = new();
    static readonly ConditionalWeakTable<ClientState, HashSet<uint>> Presented = new();
    static readonly ConditionalWeakTable<ItemCatalog, DropBuckets> CatalogBuckets = new();

    public static void Clear()
    {
        Areas.Clear();
        Presented.Clear();
    }

    static IEnumerable<ClientState> Members(ClientState conn) =>
        conn.Instance.PlayersExcept(conn.PeerId)
            .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
            .Where(p => !p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, conn.Instance)).Distinct();

    /// <summary>Populates Forge of the Titans on <see cref="GameEvents.AreaGenerated"/>.</summary>
    public static async Task Enter(ClientState conn)
    {
        if (!CrucibleTables.IsForgeArea(conn.Layout.Id) || conn.Instance is null || conn.Layout.Level is not { } level)
            return;

        var (remote, stream, ciphers) = conn.Wire;
        byte ruleset = CrucibleTables.Current.CrucibleMapRulesRow ?? InstanceCrucible.CrucibleMapRulesetRow;
        await Send(remote, stream, ciphers, InstanceProtocol.AddRuleset(ruleset),
            $"0x001f ruleset CrucibleMapRules (row {ruleset})");

        var area = Areas.GetOrCreateValue(conn.Instance);
        await area.Gate.WaitAsync();
        try
        {
            if (!area.Built)
            {
                Build(conn, area, level);
                area.Built = true;
            }
        }
        finally { area.Gate.Release(); }

        foreach (var obj in area.Objects)
            await Present(conn, obj);

        if (area.SpawnedMonsters.Count == 0)
            await SpawnEndgamePacks(conn, area, level);
    }

    static void Build(ClientState conn, ForgeAreaState area, ServerLevels.Level level)
    {
        area.Uber = CrucibleTables.IsUberForgeArea(conn.Layout.Id);
        var mapRun = MapRunOf(conn);
        area.Level = mapRun?.Area.Level is > 0 ? mapRun.Area.Level : (area.Uber ? 84 : 80);
        area.RemnantMods = mapRun?.Mods ?? MapMods.None;
        area.CombineMods = CrucibleTreeEngine.CombineModifiers.From(area.RemnantMods);

        // 1. CrucibleCombiningBench from arena_01.tmd (TileQuestObjects) or fallback near the end arena.
        var benchPlacement = level.TileQuestObjects.FirstOrDefault(o =>
            o.Type.Equals(CrucibleTables.CombiningBenchType, StringComparison.OrdinalIgnoreCase))
            ?? level.RoomObjects.FirstOrDefault(o =>
                o.Type.Equals(CrucibleTables.CombiningBenchType, StringComparison.OrdinalIgnoreCase));

        (int bx, int by, float brot) = benchPlacement is { } bp
            ? (bp.X, bp.Y, bp.Rotation)
            : (conn.Layout.Near((700, 710)).X, conn.Layout.Near((700, 710)).Y, 0f);

        var bench = new WorldObject
        {
            Id = conn.Instance.World.NewMonsterId(),
            Type = CrucibleTables.CombiningBenchType,
            X = bx,
            Y = by,
            Rotation = brot,
            TransitionState = 1,
        };
        area.CombiningBenchId = bench.Id;
        area.Objects.Add(bench);

        // 2. CrucibleUniqueForge (Antique Furnace / Scouring Furnace) when rolled on the Remnant.
        // Mods 6266 grants value 1 (non-corrupted uniques -> mode 0); Mods 6267 grants value 2 (any unique -> mode 1).
        int applyUniqueVal = area.RemnantMods.Get("map_crucible_contains_forge_that_can_apply_weapon_tree_to_unique");
        bool hasApplyUnique = applyUniqueVal > 0;
        bool hasRemoveTree = area.RemnantMods.Get("map_crucible_contains_forge_that_can_remove_weapon_tree") > 0;
        if (hasApplyUnique || hasRemoveTree)
        {
            var forgePlacement = level.RoomObjects.FirstOrDefault(o =>
                o.Type.Equals(CrucibleTables.UniqueForgeType, StringComparison.OrdinalIgnoreCase));
            (int fx, int fy, float frot) = forgePlacement is { } fp
                ? (fp.X, fp.Y, fp.Rotation)
                : (conn.Layout.Near((493, 776)).X, conn.Layout.Near((493, 776)).Y, 0f);

            uint mode = hasRemoveTree && !hasApplyUnique
                ? InstanceCrucible.ScouringForgeMode
                : (applyUniqueVal >= 2 ? InstanceCrucible.AntiqueForgeCorruptedMode : InstanceCrucible.AntiqueForgeMode);

            var forge = new WorldObject
            {
                Id = conn.Instance.World.NewMonsterId(),
                Type = CrucibleTables.UniqueForgeType,
                X = fx,
                Y = fy,
                Rotation = frot,
                TransitionState = 1,
                States = new() { ["removetreeforge"] = mode == InstanceCrucible.ScouringForgeMode ? 1 : 0 },
            };
            area.UniqueForgeId = forge.Id;
            area.UniqueForgeMode = mode;
            area.Objects.Add(forge);
        }

        // 3. CrucibleEndgameMonstersSpawnerBase pool visuals along the walkway.
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
            + $"bench=0x{area.CombiningBenchId:x} at ({bx},{by})"
            + (area.UniqueForgeId != 0 ? $", uniqueForge=0x{area.UniqueForgeId:x} mode={area.UniqueForgeMode}" : "")
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
        int moreUnique = area.RemnantMods.Get("map_crucible_unique_monsters_+%");
        int extraDamage = area.RemnantMods.Get("map_crucible_monster_damage_+%_final");
        var scaling = CrucibleMonsters.ScalingMods(area.Level, extraDamage);
        byte monsterLevel = (byte)Math.Clamp(area.Level, 1, 100);

        var (remote, stream, ciphers) = conn.Wire;
        int totalSpawned = 0;

        // Populate every second spawner (about ~22 packs along the walkway) to avoid overcrowding.
        for (int i = 0; i < spawners.Count; i++)
        {
            if (conn.Closed || conn.LeavingArea) break;
            if (i % 2 == 1 && rng.Next(100) < 55) continue;

            var sp = spawners[i];
            var theme = CrucibleMonsters.ThemeFor(rng.Next(Math.Max(1, CrucibleMonsters.Pools.Count)), rng);
            var pack = CrucibleMonsters.RollEndgamePack(tables, theme, area.Uber, moreUnique, rng);

            for (int k = 0; k < pack.Count; k++)
            {
                if (conn.Closed || conn.LeavingArea) break;
                var (varietyId, rarity) = pack[k];
                var variety = CrucibleMonsters.ResolveVariety(varietyId, theme);
                if (variety is null) continue;
                var cell = conn.Layout.Near((sp.X + (k % 3 - 1) * 4, sp.Y + (k / 3 - 1) * 4));
                uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, monsterLevel, rarity,
                    cell.X, cell.Y, unrisen: false, alone: false, extraMods: scaling);
                if (id != 0)
                {
                    area.SpawnedMonsters.Add(id);
                    totalSpawned++;
                }
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

        foreach (var member in Members(conn))
        {
            if (!Presented.TryGetValue(member, out var shown) || !shown.Contains(obj.Id)) continue;
            var (remote, stream, ciphers) = member.Wire;
            await SendWorldRecord(remote, stream, ciphers, member, obj.Id,
                InstanceEntities.TransitionableUpdate(obj.Id, (byte)trIndex, 2),
                $"0x02bc crucible {obj.Type.Split('/')[^1]} transitioned (used)");
        }
    }

    /// <summary>Handles clicks on <c>CrucibleCombiningBench</c> or <c>CrucibleUniqueForge</c>.</summary>
    public static async Task<bool> UseObject(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (!CrucibleTables.IsForgeArea(conn.Layout.Id) || conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area))
            return false;

        bool isBench = use.TargetId == area.CombiningBenchId && area.CombiningBenchId != 0;
        bool isUniqueForge = use.TargetId == area.UniqueForgeId && area.UniqueForgeId != 0;
        if (!isBench && !isUniqueForge) return false;

        var target = area.Objects.FirstOrDefault(o => o.Id == use.TargetId);
        if (target is null) return false;

        if ((isBench && area.CombiningUsed) || (isUniqueForge && area.UniqueForgeUsed))
        {
            await PlayerOf(conn).Tell("This Crucible Forge has already been used.");
            return true;
        }

        if (!await WorldOf(conn).WalkUpTo(target.X, target.Y, 16, requestedAt))
            return true;

        var (remote, stream, ciphers) = conn.Wire;
        if (isBench)
        {
            await Send(remote, stream, ciphers, InstanceCrucible.OpenCombiningUi(area.Uber),
                $"0x007b open CrucibleCombiningWindow (uber={area.Uber})");
        }
        else
        {
            await Send(remote, stream, ciphers, InstanceCrucible.OpenUniqueForgeUi(area.UniqueForgeMode, target.Id),
                $"0x007b open CrucibleUniqueForgeWindow (mode={area.UniqueForgeMode}, entity=0x{target.Id:x})");
        }
        return true;
    }

    /// <summary>Handles C→S <c>0x0299</c> (combine two weapon/shield trees at <c>CrucibleCombiningBench</c>).</summary>
    public static async Task<bool> HandleCombine(ClientState conn, byte[] bytes)
    {
        var req = InstanceCrucible.ReadCombineRequest(bytes);
        var (remote, stream, ciphers) = conn.Wire;
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area) || area.CombiningUsed)
        {
            await PlayerOf(conn).Tell("No active Crucible Combining Forge is available here.");
            return true;
        }

        var items = ItemsOf(conn);
        var stash = StashOf(conn);
        if (!CrucibleItemActions.TryLocateItem(items, stash, req.TopInventoryId, req.TopItemId, out var topTab, out var topRow, out var topItem)
            || !CrucibleItemActions.TryLocateItem(items, stash, req.BottomInventoryId, req.BottomItemId, out var botTab, out var botRow, out var botItem))
        {
            await PlayerOf(conn).Tell("Could not find both items to combine.");
            return true;
        }

        if (topTab == botTab && topRow == botRow && topItem.Id == botItem.Id)
        {
            await PlayerOf(conn).Tell("Cannot combine an item with itself.");
            return true;
        }

        var catalog = ItemsFor(conn);
        if (catalog.ByHash(topItem.Item.TypeHash) is not { } topType
            || catalog.ByHash(botItem.Item.TypeHash) is not { } botType)
            return true;

        if (!CrucibleTreeEngine.CanCombinePair(topItem.Item, topType, botItem.Item, botType, catalog.Mods, area.Uber, out string reason))
        {
            await PlayerOf(conn).Tell(reason);
            return true;
        }

        await area.Gate.WaitAsync();
        InventoryItem? updatedBottom = null;
        IReadOnlyList<InstanceCrucible.NodeMutation> mutations = [];
        try
        {
            if (area.CombiningUsed)
            {
                await PlayerOf(conn).Tell("This Crucible Forge has already been used.");
                return true;
            }

            var (combined, muts) = CrucibleTreeEngine.CombineTrees(
                topItem.Item, botItem.Item, botType, catalog, RollsFor(conn), UniquesFor(conn), area.CombineMods, Random.Shared);
            mutations = muts;

            bool committed = false;
            try
            {
                committed = Saves.Atomically(() =>
                {
                    // Destroy top item.
                    InventoryItem? removedTop = topTab is int tt
                        ? stash.Take(tt, topItem.Id)
                        : items.Remove(topRow, topItem.Id);
                    if (removedTop is null) return false;

                    // Update bottom item in place; on failure, restore in-memory top item and throw to roll back SQLite.
                    if (botTab is int bt)
                    {
                        var old = stash.Take(bt, botItem.Id);
                        if (old is null)
                        {
                            RestoreItem(items, stash, topTab, topRow, removedTop);
                            throw new CombineRollbackException();
                        }
                        var placed = stash.Place(bt, old.X, old.Y, old with { Item = combined }, ShapeOf, out _);
                        if (placed is not var (p, _))
                        {
                            stash.Place(bt, old.X, old.Y, old, ShapeOf, out _);
                            RestoreItem(items, stash, topTab, topRow, removedTop);
                            throw new CombineRollbackException();
                        }
                        updatedBottom = p;
                    }
                    else
                    {
                        updatedBottom = items.Change(botRow, botItem.Id, _ => combined);
                        if (updatedBottom is null)
                        {
                            RestoreItem(items, stash, topTab, topRow, removedTop);
                            throw new CombineRollbackException();
                        }
                    }
                    return true;
                });
            }
            catch (CombineRollbackException)
            {
                committed = false;
            }

            if (!committed || updatedBottom is null) return true;
            area.CombiningUsed = true;
        }
        finally { area.Gate.Release(); }

        // Send 0xbf removing top item and updating bottom item.
        if (topTab == botTab && (topTab is not null || topRow == botRow))
        {
            if (botTab is int tab)
                await SendTabChange(remote, stream, ciphers, conn, stash, tab, [topItem.Id, botItem.Id], [updatedBottom],
                    $"0x00bf stash tab {tab}: Crucible combine destroyed {topItem.Id}, updated {updatedBottom.Id}");
            else
                await Send(remote, stream, ciphers,
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(botRow), items.NextRevision(botRow),
                        [topItem.Id, botItem.Id], [updatedBottom]),
                    $"0x00bf inventory {PlayerInventories.InventoryId(botRow)}: Crucible combine");
        }
        else
        {
            if (topTab is int tt)
                await SendTabChange(remote, stream, ciphers, conn, stash, tt, [topItem.Id], [],
                    $"0x00bf stash tab {tt}: Crucible combine destroyed {topItem.Id}");
            else
                await Send(remote, stream, ciphers,
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(topRow), items.NextRevision(topRow), [topItem.Id], []),
                    $"0x00bf inventory {PlayerInventories.InventoryId(topRow)}: Crucible combine destroyed {topItem.Id}");

            if (botTab is int bt)
                await SendTabChange(remote, stream, ciphers, conn, stash, bt, [botItem.Id], [updatedBottom],
                    $"0x00bf stash tab {bt}: Crucible combine updated {updatedBottom.Id}");
            else
                await Send(remote, stream, ciphers,
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(botRow), items.NextRevision(botRow), [botItem.Id], [updatedBottom]),
                    $"0x00bf inventory {PlayerInventories.InventoryId(botRow)}: Crucible combine updated {updatedBottom.Id}");
        }

        uint resultInvId = botTab is int rtab ? stash.InventoryOf(rtab) : PlayerInventories.InventoryId(botRow);
        await Send(remote, stream, ciphers, InstanceCrucible.CombineResult(resultInvId, updatedBottom.Id, mutations),
            $"0x029a Crucible combine result inv={resultInvId} item={updatedBottom.Id} mutations={mutations.Count}");

        if (area.Objects.FirstOrDefault(o => o.Id == area.CombiningBenchId) is { } benchObj)
            await TransitionUsed(conn, benchObj);

        if (botTab is null && CrucibleItemActions.IsEquipmentRow(botRow))
            await WorldOf(conn).RefreshStats("Crucible combine");

        Console.WriteLine($"[crucible] {CharacterName(conn)} combined {topType.Name} into {botType.Name} ({mutations.Count} mutation(s))");
        return true;
    }

    /// <summary>Handles C→S <c>0x029b</c> (Antique Furnace add tree to Unique / Scouring Furnace remove tree).</summary>
    public static async Task<bool> HandleUniqueForge(ClientState conn, byte[] bytes)
    {
        var req = InstanceCrucible.ReadUniqueForgeRequest(bytes);
        var (remote, stream, ciphers) = conn.Wire;
        if (conn.Instance is null || !Areas.TryGetValue(conn.Instance, out var area) || area.UniqueForgeId == 0 || area.UniqueForgeUsed)
        {
            await PlayerOf(conn).Tell("No active Antique or Scouring Furnace is available here.");
            return true;
        }

        var items = ItemsOf(conn);
        var stash = StashOf(conn);
        if (!CrucibleItemActions.TryLocateItem(items, stash, req.InventoryId, req.ItemId, out var tab, out var row, out var target))
        {
            await PlayerOf(conn).Tell("Target item not found.");
            return true;
        }

        var catalog = ItemsFor(conn);
        if (catalog.ByHash(target.Item.TypeHash) is not { } type) return true;

        var result = CrucibleTreeEngine.ApplyUniqueForge(target.Item, type, catalog.Mods, area.UniqueForgeMode, Random.Shared, out string reason);
        if (result is null)
        {
            await PlayerOf(conn).Tell(reason);
            return true;
        }

        InventoryItem? updated = null;
        await area.Gate.WaitAsync();
        try
        {
            if (area.UniqueForgeUsed)
            {
                await PlayerOf(conn).Tell("This furnace has already been used.");
                return true;
            }
            bool committed = Saves.Atomically(() =>
            {
                if (tab is int t)
                {
                    var old = stash.Take(t, target.Id);
                    if (old is null) return false;
                    var placed = stash.Place(t, old.X, old.Y, old with { Item = result }, ShapeOf, out _);
                    if (placed is not var (p, _)) return false;
                    updated = p;
                }
                else
                {
                    updated = items.Change(row, target.Id, _ => result);
                    if (updated is null) return false;
                }
                return true;
            });
            if (!committed || updated is null) return true;
            area.UniqueForgeUsed = true;
        }
        finally { area.Gate.Release(); }

        uint invId = tab is int st ? stash.InventoryOf(st) : PlayerInventories.InventoryId(row);
        if (tab is int sendTab)
            await SendTabChange(remote, stream, ciphers, conn, stash, sendTab, [target.Id], [updated],
                $"0x00bf stash tab {sendTab}: Crucible Unique/Scouring Forge updated {updated.Id}");
        else
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(invId, items.NextRevision(row), [target.Id], [updated]),
                $"0x00bf inventory {invId}: Crucible Unique/Scouring Forge updated {updated.Id}");

        await Send(remote, stream, ciphers, InstanceCrucible.UniqueForgeResult(invId, updated.Id),
            $"0x029c Crucible Unique/Scouring Forge result inv={invId} item={updated.Id}");

        if (area.Objects.FirstOrDefault(o => o.Id == area.UniqueForgeId) is { } forgeObj)
            await TransitionUsed(conn, forgeObj);

        if (tab is null && CrucibleItemActions.IsEquipmentRow(row))
            await WorldOf(conn).RefreshStats("Crucible Unique/Scouring Forge");

        Console.WriteLine($"[crucible] {CharacterName(conn)} used CrucibleUniqueForge mode={area.UniqueForgeMode} on {type.Name} ({updated.Id})");
        return true;
    }

    /// <summary>Applies Remnant monster drop modifiers in Forge of the Titans on <see cref="GameEvents.Drops"/>.</summary>
    public static void OnDrops(DropRoll drop)
    {
        if (drop.Cancel || !CrucibleTables.IsForgeArea(drop.Conn.Layout.Id)) return;
        if (drop.Conn.Instance is null || !Areas.TryGetValue(drop.Conn.Instance, out var area)) return;

        bool isRareOrUnique = drop.Monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique;
        if (!isRareOrUnique) return;

        var rng = Random.Shared;
        var catalog = ItemsFor(drop.Conn);
        var mods = area.RemnantMods;

        int oreChance = mods.Get("map_crucible_rare_unique_monster_drop_itemised_weapon_tree_experience_%_chance");
        if (oreChance > 0 && rng.Next(100) < oreChance && catalog.Find(CrucibleTables.MagmaticOreId) is { Supported: true } oreType)
        {
            drop.Made.Add(ItemCatalog.Create(oreType) with { ItemLevel = (byte)Math.Clamp(area.Level, 1, 100), StoredExperience = CrucibleTables.RemnantOreExperience });
        }

        var buckets = BucketsFor(catalog);
        TryDropTreeWeapon(drop, catalog, rng, mods.Get("map_crucible_rare_unique_monster_drop_melee_weapon_with_tree_%_chance"),
            buckets.Melee, area.Level);
        TryDropTreeWeapon(drop, catalog, rng, mods.Get("map_crucible_rare_unique_monster_drop_ranged_weapon_with_tree_%_chance"),
            buckets.Ranged, area.Level);
        TryDropTreeWeapon(drop, catalog, rng, mods.Get("map_crucible_rare_unique_monster_drop_shield_with_tree_%_chance"),
            buckets.Shields, area.Level);

        if (drop.Monster.Rarity == MonsterRarity.Unique)
        {
            int uniqueTreeChance = mods.Get("map_crucible_unique_monster_drop_unique_with_weapon_tree_%_chance");
            if (uniqueTreeChance > 0 && rng.Next(100) < uniqueTreeChance)
            {
                var uniques = UniquesFor(drop.Conn).CollectionItems(rng);
                var eligible = uniques
                    .Where(u => u.Item is { } it && catalog.ByHash(it.TypeHash) is { } bt
                        && CrucibleTreeEngine.CanHaveTree(it, bt, catalog.Mods))
                    .ToList();
                if (eligible.Count > 0)
                {
                    var picked = eligible[rng.Next(eligible.Count)].Item!;
                    var bt = catalog.ByHash(picked.TypeHash)!;
                    var tree = CrucibleTreeEngine.GenerateTree(picked, bt, catalog.Mods, rng);
                    drop.Made.Add(picked with { WeaponPassiveTree = tree });
                }
            }
        }
    }

    static void RestoreItem(PlayerInventories items, AccountStash stash, int? tab, byte row, InventoryItem item)
    {
        if (tab is int t) stash.Place(t, item.X, item.Y, item, ShapeOf, out _);
        else items.Put(row, item);
    }

    static DropBuckets BucketsFor(ItemCatalog catalog) =>
        CatalogBuckets.GetValue(catalog, c =>
        {
            var melee = new List<ItemCatalog.BaseType>();
            var ranged = new List<ItemCatalog.BaseType>();
            var shields = new List<ItemCatalog.BaseType>();
            foreach (var t in c.All)
            {
                if (!t.Supported || !t.Components.Contains("Mods")) continue;
                if (MeleeClassRows.Contains(t.ClassRow)) melee.Add(t);
                else if (RangedClassRows.Contains(t.ClassRow)) ranged.Add(t);
                else if (t.ClassRow == ShieldClassRow) shields.Add(t);
            }
            return new DropBuckets(melee, ranged, shields);
        });

    static void TryDropTreeWeapon(
        DropRoll drop, ItemCatalog catalog, Random rng, int chance,
        IReadOnlyList<ItemCatalog.BaseType> bucket, int areaLevel)
    {
        if (chance <= 0 || rng.Next(100) >= chance || bucket.Count == 0) return;
        int eligibleCount = 0;
        ItemCatalog.BaseType? chosen = null;
        foreach (var t in bucket)
        {
            if (t.DropLevel > areaLevel) continue;
            eligibleCount++;
            if (rng.Next(eligibleCount) == 0) chosen = t;
        }
        if (chosen is null) return;

        var item = RollsFor(drop.Conn).Roll(ItemCatalog.Create(chosen) with { ItemLevel = (byte)Math.Clamp(areaLevel, 1, 100) },
            ItemRarity.Rare, rng);
        var tree = CrucibleTreeEngine.GenerateTree(item, chosen, catalog.Mods, rng);
        drop.Made.Add(item with { WeaponPassiveTree = tree });
    }
}
