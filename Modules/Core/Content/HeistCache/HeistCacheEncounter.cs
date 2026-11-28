using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.HeistCache;

/// <summary>
/// One instance's Smuggler's Caches: where they stand, the click, and the open. The state belongs to the instance, not
/// to a player: a player who enters is shown the same caches, an opened one back open.
/// <list type="bullet">
/// <item>The create is a measured 39-byte body of one cache (level 70): no StateMachine segment, the extra 0x01 at
/// offset 16, ObjectMagicProperties 08 00 00 00. The cache's component list has no StateMachine, so the Strongbox
/// writers must not be used here.</item>
/// <item>The Chest component reads its opened bit on creation. Its update reader returns immediately, so opening
/// the cache replaces its closed presentation. An arriving player receives the same opened create.</item>
/// <item>There is no daemon, no guard wave and no frame clock: the open is immediate, on the click's frame.</item>
/// </list>
/// </summary>
public sealed class HeistCacheEncounter
{
    public sealed class Box
    {
        public uint Id;
        public required (int X, int Y) Cell;
        public required int Level;
        /// <summary>closed, open.</summary>
        public string Stage = "closed";
        /// <summary>The area this box was last created in, so a re-made area is shown it again.</summary>
        public object? Shown;
    }

    public sealed class State
    {
        public readonly List<Box> Boxes = [];
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Placed;
        public string Why = "";
    }

    const string StateKey = "HeistCache.Boxes";
    readonly ConditionalWeakTable<InstanceState, State> _states = new();

    /// <summary>The instance's state, made (and read back from the instance's saved text) on first use.</summary>
    public State For(Player player) => _states.GetValue(player.Instance, instance =>
    {
        var state = new State();
        if (instance.ContentState.TryGetValue(StateKey, out var text))
            foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(',');
                if (f.Length == 5 && uint.TryParse(f[0], out uint id) && int.TryParse(f[1], out int x)
                    && int.TryParse(f[2], out int y) && int.TryParse(f[3], out int level))
                    state.Boxes.Add(new Box { Id = id, Cell = (x, y), Level = level, Stage = f[4] });
            }
        if (state.Boxes.Count > 0) state.Placed = true;
        return state;
    });

    /// <summary>The instance's state when it has caches, without making one for every area a click happens in.</summary>
    State? Existing(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (_states.TryGetValue(instance, out var state)) return state.Boxes.Count > 0 ? state : null;
        return instance.ContentState.ContainsKey(StateKey) ? For(player) : null;
    }

    void Save(Player player, State state) =>
        player.Instance.ContentState[StateKey] = string.Join(";", state.Boxes.Select(b => $"{b.Id},{b.Cell.X},{b.Cell.Y},{b.Level},{b.Stage}"));

    /// <summary>Module unload: drop the in-memory states; the instance's own text restores them.</summary>
    public void Clear() => _states.Clear();

    // ---- placement ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A new instance's population: as many caches as the Heist row gives it (the client's WorldAreaLeagueChances,
    /// column 18), each on a walkable cell with room, apart from the others. Deterministic in the area and its seed.
    /// Runs in Acts 6–10 campaign areas (11–15% per WorldAreaLeagueChances; Acts 1–5 naturally roll 0%) and maps,
    /// skipping towns, hideouts, Twilight Strand, and Labyrinth.
    /// </summary>
    public async Task OnAreaPopulated(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (layout.Walkable is null || IsNonCombatHub(player)
            || layout.Id == TutorialObjects.Strand
            || LabyrinthTrials.InLabyrinth(layout.Id)) return;
        var state = For(player);
        await state.Gate.WaitAsync();
        try
        {
            if (state.Placed) return;
            state.Placed = true;
            var stats = player.Instance.AreaStats(() => AreaLeagueStats(player));
            int S(uint name) => stats.GetValueOrDefault(name);
            if (S(Stat.MapDisableLeagues) > 0 || S(Stat.AtlasKeystoneNoExtraContent) > 0 || S(Stat.MapNukeEverything) > 0
                || S(Stat.MapAreaUnderlevelled) > 0 || S(Stat.MapDisableHeistFromChance) > 0)
            { state.Why = "disabled by the map's stats"; return; }
            // The encounter's own draws, apart from the packs' (the same instance gets the same caches). When the map
            // zeroes the base chance, only the added chance rolls: the base is scaled to nothing, not the whole roll.
            var rng = new Random(area.Seed ^ 0x48656973);
            bool hardMode = player.LeagueModule?.HardMode == true;
            int count = LeagueChances.Current.Count(layout.Id, LeagueChances.Heist, rng, alternate: hardMode,
                increased: S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0 ? -100 : S(Stat.MapLeagueBaseChancePlusPctFinal),
                added: S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapSmugglersCacheAdditionalChancePct),
                encounterIncrease: S(Stat.MapHeistSmugglersCacheEncounterChancePlusPct));
            state.Why = $"league roll Heist {count}";
            if (count <= 0) return;
            await PlaceBoxes(player, area.Level, area.Taken, count, rng, state);
            Console.WriteLine($"[heist-cache] {layout.Id}: {state.Why}, {state.Boxes.Count} placed");
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>Place <paramref name="count"/> caches in this instance, on the encounter's own draws.</summary>
    internal async Task PlaceBoxes(Player player, byte level, IReadOnlyList<(int X, int Y)> taken, int count, Random rng, State state)
    {
        var layout = player.Layout;
        var takenCells = taken.ToList();
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        for (int i = 0; i < count; i++)
        {
            if (AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, layout.Walkable!, (layout.SpawnX, layout.SpawnY),
                    transitions, takenCells, rng, HeistCacheTables.PlaceApart,
                    featureAreas: layout.Level?.FeatureAreas) is not { } cell) break;
            takenCells.Add(cell);
            var box = new Box { Cell = cell, Level = level };
            state.Boxes.Add(box);
            await Present(player, box);
            Save(player, state);
        }
    }

    /// <summary>
    /// A player entered the area: every cache of the instance is shown to the area (once; a player who enters later is
    /// given it with the area's objects) and blocks this player's own walk. A saved instance whose area was made again
    /// gets its caches created again, on new ids, an opened one open.
    /// </summary>
    public async Task OnPlayerEntered(Player player)
    {
        if (Existing(player) is not { } state) return;
        await state.Gate.WaitAsync();
        try
        {
            foreach (var box in state.Boxes.ToArray())
            {
                if (!ReferenceEquals(box.Shown, player.Area))
                {
                    if (player.Area.Objects.Has(box.Id)) box.Shown = player.Area;
                    else { box.Id = WorldOf(player).NewObjectId(); await Present(player, box); Save(player, state); }
                }
                player.SetObjectBlocker(box.Id, box.Cell.X, box.Cell.Y, HeistCacheTables.BoxSize, true);
            }
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>
    /// The cache's create blob for a cache at <paramref name="cell"/> of <paramref name="level"/> (the area level),
    /// <paramref name="opened"/> when the instance has already opened it. The offsets: 0-1 flags 00 00; 2-5 x, 6-9 y
    /// (LE i32); 10-13 facing f32 0; 14-15 u16 0; 16: 0x01 (the extra byte vs a strongbox); 17: 00; 18: 00 Animated;
    /// 19: opened; 20: level; 21: 00; 22: Stats 0 pairs + the 9-byte tail (u32 0, f32 3.0, u8 1); 32-35
    /// ObjectMagicProperties 08 00 00 00; 36: 01 InteractionAction; 37: 00 MinimapIcon; 38: 00 tasks. No StateMachine
    /// segment: the cache's own component list has none, and a create that lists more would shift every later field.
    /// </summary>
    public static byte[] CreateBody((int X, int Y) cell, int level, bool opened)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(cell.X).I32(cell.Y).F32(0).U16(0).U8(0x01).U8(0x00); // Positioned: 0x01 at offset 16 [measured]
        w.U8(0x00);                                               // Animated: the type's own model
        w.U8(opened ? (byte)1 : (byte)0).U8((byte)Math.Clamp(level, 0, 255)).U8(0); // Chest: opened@19, level@20
        w.U8(0x00).U32(0).U32(0x40400000).U8(0x01);               // Stats: 0 pairs + tail [measured]
        w.U16(0x0008).U8(0x00).U8(0x00);                         // ObjectMagicProperties 08 00 00 00 [measured]
        w.U8(0x01);                                               // InteractionAction
        w.U8(0x00);                                               // MinimapIcon
        return w.U8(0x00).ToArray();                              // tasks
    }

    /// <summary>Create the box in the player's area, as the area's object: every client of the area is given it, and a
    /// client that joins later is given it with the area's objects. Called under the state's gate.</summary>
    async Task Present(Player player, Box box)
    {
        var world = WorldOf(player);
        if (box.Id == 0) box.Id = world.NewObjectId();
        player.SetObjectBlocker(box.Id, box.Cell.X, box.Cell.Y, HeistCacheTables.BoxSize, true);
        bool opened = box.Stage == "open";
        // CreateInWakeZone already appends the wake bytes, so no wake: true here or the wake goes out twice. The factory
        // builds the bytes afresh for each client.
        await SendWorldCreate(player, box.Id, () =>
            InstanceEntities.CreateInWakeZone(box.Id, InstanceEntities.ObjectTypeHash(HeistCacheTables.Path),
                CreateBody(box.Cell, box.Level, opened)),
            $"Smuggler's Cache 0x{box.Id:x} level {box.Level} at {box.Cell} ({box.Stage})");
        box.Shown = player.Area;
    }

    // ---- the click ----------------------------------------------------------------------------------------------------

    /// <summary>A click on one of this instance's caches: the character walks up to it and it opens there,
    /// immediately. A cache spawns nothing, so a click in the arrival's grace period needs no guard beyond the walk's own.
    /// </summary>
    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Existing(player) is not { } state) return false;
        Box? box;
        lock (state) box = state.Boxes.FirstOrDefault(b => b.Id == use.TargetId && b.Id != 0);
        if (box is null) return false;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (box.Stage != "closed") return true;
        if (!TryBeginObjectWalk(player, requestedAt, box.Cell.X, box.Cell.Y, HeistCacheTables.BoxSize, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, box.Cell.X, box.Cell.Y,
                use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player opens Smuggler's Cache 0x{box.Id:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        try
        {
            if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || !PlayerActionCurrent(player, action)
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), box.Cell, HeistCacheTables.BoxSize)) return true;
            await Open(player, box, state);
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
                still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    const string HeistCoinId = "Metadata/Items/Heist/HeistCoin";
    const string RampartsAreaId = "2_8_1";
    const string RampartsCacheFlag = "RampartsSmugglersCacheOpened";
    const string UsedHeistCoinFlag = "UsedHeistCoin";

    /// <summary>Present the opened cache once when players finish their walks together, then drop retail Heist loot.</summary>
    public async Task<bool> Open(Player player, Box box, State? state = null)
    {
        state ??= For(player);
        await state.Gate.WaitAsync();
        try
        {
            if (box.Stage != "closed") return false;
            box.Stage = "open";
            Save(player, state);
            await SendWorldRemoval(player, box.Id);
            await Present(player, box);
        }
        finally { state.Gate.Release(); }
        await DropLoot(player, box);
        return true;
    }

    static async Task DropLoot(Player player, Box box)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0" || !GameEvents.AllowsLoot(player)) return;
        var stats = player.Instance.AreaStats(() => AreaLeagueStats(player));
        if (stats.GetValueOrDefault(Stat.MapDisableHeistCoinContractBlueprintDrops) > 0) return;

        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        int level = Math.Clamp(box.Level > 0 ? box.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
        bool isCampaign = !MapDevices.IsMapArea(player.Layout.Hash);
        bool hardMode = player.LeagueModule?.HardMode == true;
        var rng = new Random(HashCode.Combine(player.Seed, box.Cell.X, box.Cell.Y, level, 0x48434143));
        var drops = new List<ItemDrops.Drop>();

        // 1. Rogue's Markers (Metadata/Items/Heist/HeistCoin), scaled by area level.
        if (catalog.Find(HeistCoinId) is { Supported: true } coinType)
        {
            int count = level >= 68
                ? 120 + (level - 68) * 10 + rng.Next(40, 141)
                : 45 + Math.Max(0, level - 45) * 3 + rng.Next(15, 56);
            // First Smuggler's Cache opened in Acts 6-10 guarantees enough Rogue's Markers to introduce Rogue Harbour.
            if (isCampaign && !player.Character.QuestFlags.Contains(UsedHeistCoinFlag))
                count = Math.Max(count, 120);
            int doublePct = stats.GetValueOrDefault(Stat.HeistCoinsFromWorldChestsDoublePct);
            if (doublePct > 0 && rng.Next(100) < doublePct) count *= 2;
            ushort stack = (ushort)Math.Clamp(count, 1, Math.Max(1, (int)coinType.MaxStack));
            drops.Add(new(ItemCatalog.Create(coinType) with { StackSize = stack }, coinType));
        }

        // 2. Contracts (DropLevel <= areaLevel, L48-60+; filtered by Supported so wire serialization never throws).
        var contracts = catalog.All
            .Where(t => t.Class == "HeistContract" && t.Supported && t.DropLevel <= level
                && !t.Id.Contains("/QuestContracts/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (contracts.Length > 0)
        {
            int contractCount = rng.Next(100) < 50 ? 2 : 1;
            for (int i = 0; i < contractCount; i++)
            {
                var cType = contracts[rng.Next(contracts.Length)];
                var item = ItemCatalog.Create(cType) with { ItemLevel = (byte)level };
                item = RollHeistItemRarity(item, rolls, rng);
                drops.Add(new(item, cType));
            }
        }

        // 3. Blueprints (DropLevel <= areaLevel, L50+; fully wire-supported via HeistBlueprintState).
        if (level >= 50)
        {
            int bpChance = contracts.Length > 0 ? 15 : 35;
            if (rng.Next(100) < bpChance)
            {
                var blueprints = catalog.All
                    .Where(t => t.Class == "HeistBlueprint" && t.Supported && t.DropLevel <= level)
                    .ToArray();
                if (blueprints.Length > 0)
                {
                    var bpType = blueprints[rng.Next(blueprints.Length)];
                    if (BuildBlueprint(bpType, level, hardMode, rng) is { } bpItem)
                    {
                        bpItem = RollHeistItemRarity(bpItem, rolls, rng);
                        drops.Add(new(bpItem, bpType));
                    }
                }
            }
        }

        if (isCampaign && player.Layout.Id == RampartsAreaId
            && !player.Character.QuestFlags.Contains(RampartsCacheFlag))
            await SetQuestFlags(player, [RampartsCacheFlag]);

        foreach (var drop in ItemDrops.Stacked(drops))
        {
            var at = player.Layout.Near((box.Cell.X + rng.Next(-4, 5), box.Cell.Y + rng.Next(-4, 5)));
            var ground = new WorldItemObject(at.X, at.Y, drop.Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, $"Smuggler's Cache drops {drop.Type.Name}", justDropped: true);
        }
    }

    static Item RollHeistItemRarity(Item item, ItemRolls rolls, Random rng)
    {
        int roll = rng.Next(100);
        if (roll < 10) return rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
        if (roll < 35) return rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = false };
        return item;
    }

    static Item? BuildBlueprint(ItemCatalog.BaseType type, int areaLevel, bool hardMode, Random rng)
    {
        if (!ClientTables.Has("HeistAreas") || !ClientTables.Has("HeistChestRewardTypes")) return null;
        var areas = ClientTables.Table("HeistAreas").Where(r => r.IntOrNull("Blueprint_BaseItemTypesKey") == type.Row).ToArray();
        if (areas.Length == 0) return null;
        var jobs = areas[0].Ints("HeistJobsKeys").Where(j => j is >= 0 and < 255).ToArray();
        if (jobs.Length == 0) return null;
        var rewards = ClientTables.Table("HeistChestRewardTypes");
        int escape = -1;
        var rooms = new List<(int Row, int Min, int Max, int Weight, HashSet<int> Jobs)>();
        for (int row = 0; row < rewards.Count; row++)
        {
            var r = rewards[row];
            string rid = r.Text("Id") ?? "";
            if (rid == "EscapeRoom") { escape = row; continue; }
            if (!hardMode && rid.StartsWith("HardMode", StringComparison.OrdinalIgnoreCase)) continue;
            int weight = r.IntOrNull("Weight") ?? 0;
            if (weight <= 0 || row >= 128) continue;
            rooms.Add((row, r.IntOrNull("MinLevel") ?? 0, r.IntOrNull("MaxLevel") ?? 100, weight, r.Ints("HeistJobsKey").ToHashSet()));
        }
        if (escape < 0 || escape >= 128) return null;

        byte bpLevel = (byte)Math.Clamp(areaLevel, 50, 83);
        int maxJobLevel = Math.Clamp((bpLevel - 40) / 10 + 1, 1, 5);
        int wingCount = rng.Next(3, 5);
        var wings = new List<HeistWing>(wingCount);
        for (int w = 0; w < wingCount; w++)
        {
            bool revealed = w == 0;
            int jobCount = Math.Min(jobs.Length, rng.Next(2, 4));
            var pickedJobs = jobs.OrderBy(_ => rng.Next()).Take(jobCount).ToArray();
            var allowed = rooms.Where(r => r.Min <= bpLevel && r.Max >= bpLevel && r.Jobs.Overlaps(pickedJobs)).ToArray();
            if (allowed.Length == 0) allowed = rooms.Where(r => r.Min <= bpLevel && r.Max >= bpLevel).ToArray();
            if (allowed.Length == 0) return null;

            int totalWeight = allowed.Sum(r => r.Weight);
            int roomCount = rng.Next(3, 7);
            var wingRooms = new List<HeistRoom>(roomCount + 2);
            for (int r = 0; r < roomCount; r++)
            {
                int draw = rng.Next(totalWeight);
                var selected = allowed[^1];
                foreach (var candidate in allowed)
                    if ((draw -= candidate.Weight) < 0) { selected = candidate; break; }
                wingRooms.Add(new((byte)selected.Row, revealed && r < 2));
            }
            wingRooms.Add(new((byte)escape, revealed));
            wingRooms.Add(new((byte)escape, false));

            wings.Add(new(
                revealed,
                [.. pickedJobs.Select(j => new HeistJob((byte)j, (byte)rng.Next(1, maxJobLevel + 1)))],
                [.. Enumerable.Repeat((byte)0xff, jobCount)],
                wingRooms));
        }

        uint seed = (uint)rng.NextInt64(1, (long)uint.MaxValue + 1);
        return ItemCatalog.Create(type) with
        {
            ItemLevel = bpLevel,
            HeistBlueprint = new HeistBlueprintState(seed, bpLevel, 1, false, wings),
        };
    }
}
