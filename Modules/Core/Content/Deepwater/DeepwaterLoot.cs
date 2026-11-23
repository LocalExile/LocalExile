using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>What a run holds besides its monsters: the Dead Man's Sulphur nodes a lantern's light collects, and the chests
/// and interactables the player opens in the light (Sunken Loot, Gold Pile, Treasure Clam, Message in a Bottle, Golden
/// Lantern, Soothing Coral, and in a Voyage Allflame Kindling). Placed once per run from the instance seed; nothing is
/// used outside the light ("Cannot interact with this unless it is within Lanternlight", ClientStrings 8447). The
/// counts, size mix, weights and amounts are guesses (marked G) where the client data gives none.</summary>
internal static class DeepwaterLoot
{
    public enum Kind { Node, SunkenLoot, GoldPile, Clam, Bottle, GoldenLantern, SoothingCoral, Kindling, PiratesLocker, DrownedOffering, RotmothersStash, TormentCage, IzaroAltar, TreasureAnchor }

    /// <summary>A node, chest or interactable of the run: its type and place, its object id now (an opened chest is created
    /// again under a new id, as Chest has no update reader), a node's Sulphur, and whether it was collected or used.</summary>
    public sealed class Thing
    {
        public required Kind Kind { get; init; }
        public required string Type { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required int Cell { get; init; }
        public float Rotation { get; init; }
        /// <summary>The model when the type has none of its own (a Chests row's AOFiles).</summary>
        public string? Model { get; init; }
        public uint Id { get; set; }
        public int Sulphur { get; init; }
        /// <summary>Placed by the room itself (Level.RoomObjects), not scattered.</summary>
        public bool FromRoom { get; init; }
        /// <summary>A Sulphur corpse's pose, its <c>this.RandomNumber</c> (0..7). Kept, so a re-entry shows the same corpse.</summary>
        public int Pose { get; init; }
        public bool Done { get; set; }
    }

    const string Objects = "Metadata/Terrain/Leagues/Deepwater/Objects/";
    const string Chests = "Metadata/Chests/LeagueDeepwater/";
    const string RotmothersStashType = Chests + "DeepwaterChestHazardBoat";
    const string TormentCageType = Objects + "DeepwaterTormentedSpiritEncounter";
    const string IzaroAltarType = Objects + "DeepwaterIzaroObject";
    const string IzaroChestFallback = "Metadata/Chests/Labyrinth/Izaro/IzaroChest1";
    const string OfferingToTheGoddessId = "Metadata/Items/MapFragments/CurrencyOfferingToTheGoddess";
    const string LeagueMonsters = "Metadata/Monsters/DeepwaterLeague/";
    const string BottleItem = "Metadata/Items/Deepwater/DeepwaterBottledItem";
    const uint ResourceFound = Stat.MapDeepwaterLeagueResourceFoundPlusPct;

    static readonly string[] TreasureAnchorChests =
    [
        Chests + "CurrencyTreasureChestOpulent",
        Chests + "DeepwaterChestStackedDecks",
        Chests + "DeepwaterChestScarabs",
        Chests + "DeepwaterAnchorUniqueArmour",
        Chests + "DeepwaterAnchorUniqueWeapon",
        Chests + "DeepwaterAnchorUniqueJewellery",
        Chests + "DeepwaterChestRareJewellery",
    ];

    /// <summary>The Sulphur nodes by size and the world.db <c>system_settings</c> key of their share (G, server policy);
    /// each type's own <c>base_resource_multiplier</c> (50/100/150/200) sets its amount.</summary>
    static readonly (string Type, string Share)[] Nodes =
    [
        (Objects + "ResourceChestSmall", "deepwater_sulphur_node_share_small"), (Objects + "ResourceChestBase", "deepwater_sulphur_node_share_base"),
        (Objects + "ResourceChestLarge", "deepwater_sulphur_node_share_large"), (Objects + "ResourceChestHuge", "deepwater_sulphur_node_share_huge"),
    ];

    /// <summary>The scattered objects of this step: their kind, type, the <c>system_settings</c> key of their weight when
    /// their Chests row has no <c>__col7</c> (G, server policy: Gold, Clam and Bottle rows have 0, the interactables no
    /// row), and whether only a Voyage has them (the wiki: Kindling).</summary>
    static readonly (Kind Kind, string Type, string Fallback, bool VoyageOnly)[] Scattered =
    [
        (Kind.SunkenLoot, Chests + "CurrencyTreasureChest", "deepwater_weight_sunken_loot", false),
        (Kind.GoldPile, Chests + "GoldTreasureChest", "deepwater_weight_gold_pile", false),
        (Kind.Clam, Chests + "ClamTreasureChest", "deepwater_weight_treasure_clam", false),
        (Kind.Bottle, Chests + "BottledItemChest", "deepwater_weight_message_in_a_bottle", false),
        (Kind.PiratesLocker, Chests + "RandomDucatChest", "deepwater_weight_pirates_locker", false),
        (Kind.DrownedOffering, DeepwaterCursedDucats.DrownedOfferingObject, "deepwater_weight_offering_of_the_drowned", false),
        (Kind.GoldenLantern, Objects + "DeepwaterGoldenLantern", "deepwater_weight_golden_lantern", false),
        (Kind.SoothingCoral, Objects + "DeepwaterFlaskRefill", "deepwater_weight_soothing_coral", false),
        (Kind.Kindling, Objects + "DeepwaterLanternReplenishEncounter", "deepwater_weight_allflame_kindling", true),
    ];

    /// <summary>A world.db <c>system_settings</c> value (0-10000); 0 without its row.</summary>
    internal static int Setting(string key) => SystemSettings.Default.Values.TryGetValue(key, out var v) && double.IsFinite(v) ? (int)Math.Clamp(v, 0, 10_000) : 0;

    static int SettingOr(string key, int fallback) =>
        SystemSettings.Default.Values.TryGetValue(key, out var v) && double.IsFinite(v) ? (int)Math.Clamp(v, 0, 10_000) : fallback;

    // G: the spacing of the scattered objects and nodes, and the clear ring around the start Bathysphere.
    const int ThingsApart = 15, NodesApart = 12, StartClear = 20, Tries = 200;
    /// <summary>Message in a Bottle's Chest <c>item_drop_delay</c>.</summary>
    const double BottleDelay = 2.3;

    // Chests rows by Id: __col7 (read as a spawn weight, G), AOFiles, and InheritsFrom (for chests without their own .ot).
    static Dictionary<string, (int Weight, string[] Models, string? InheritsFrom)> ChestRows = [];
    static Dictionary<string, StateMachineData?> States = [];
    static Dictionary<int, (int Min, int Max, int Packs)> RoomRanges = [];
    static HashSet<int> SpecialRooms = [];
    static Dictionary<int, int> Balance = [];

    public static void LoadGameData()
    {
        ChestRows = ClientTables.TableOrEmpty("Chests")
            .Where(r => r.Text("Id") is { } id && id.StartsWith(Chests, StringComparison.Ordinal))
            .ToDictionary(r => r.Text("Id")!, r => (r.IntOrNull("__col7") ?? 0, r.Texts("AOFiles").ToArray(), r.TextOrNull("InheritsFrom")));

        var wanted = Nodes.Select(n => n.Type)
            .Concat(Scattered.Select(s => s.Type))
            .Append(RotmothersStashType)
            .Append(TormentCageType)
            .Append(IzaroAltarType)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in ChestRows.Values)
            if (row.InheritsFrom is { Length: > 0 } parent) wanted.Add(parent);
        var map = StatesOf(wanted);
        foreach (var (id, row) in ChestRows)
            if (!map.ContainsKey(id) && row.InheritsFrom is { Length: > 0 } parent && map.TryGetValue(parent, out var inherited))
                map[id] = inherited;
        States = map;

        RoomRanges = ClientTables.TableOrEmpty("DeepwaterRooms")
            .ToDictionary(r => r.Index, r => (r.IntOrNull("__col10") ?? 0, r.IntOrNull("__col11") ?? 0, r.IntOrNull("__col8") ?? 0));

        SpecialRooms = ClientTables.TableOrEmpty("DeepwaterRooms")
            .Where(r => r.BoolOrNull("Special") == true).Select(r => r.Index).ToHashSet();

        Balance = ClientTables.TableOrEmpty("DeepwaterBalancePerLevel")
            .Where(r => r.IntOrNull("__col0") is not null).ToDictionary(r => r.Int("__col0"), r => r.IntOrNull("__col1") ?? 0);
    }

    /// <summary>These types' StateMachine defaults (their .ot's shared states), by type.</summary>
    internal static Dictionary<string, StateMachineData?> StatesOf(IReadOnlySet<string> types)
    {
        var shared = ObjectTypes.SharedStates();
        return ObjectTypes.All().Where(t => types.Contains(t.Type)).ToDictionary(t => t.Type, t => ObjectTypes.StateMachine(t.Row, shared));
    }

    /// <summary>Whether <paramref name="roomId"/> is a special <c>DeepwaterRooms</c> room (O(1) lookup).</summary>
    public static bool IsSpecialRoom(int roomId) => roomId >= 0 && SpecialRooms.Contains(roomId);

    /// <summary>DeepwaterBalancePerLevel <c>__col1</c> at a level (−66 at 1, 0 at 67, +144 from 85), clamped to the table.</summary>
    static int BalanceAt(int level)
    {
        var table = Balance;
        if (table.Count == 0) return 0;
        return table.GetValueOrDefault(Math.Clamp(level, table.Keys.Min(), table.Keys.Max()));
    }

    /// <summary>A node's Sulphur (G): <c>round(multiplier × (100 + balance(level)) / 100 × (100 + found%) / 100)</c>.</summary>
    static int Amount(int multiplier, int level, int found) =>
        Math.Max(0, (int)Math.Round(multiplier * (100 + BalanceAt(level)) / 100.0 * Math.Max(0, 100 + found) / 100.0));

    static QuestObjectTypes.Info? Info(string type)
    {
        var info = QuestObjectTypes.Find(type)
            ?? (ChestRows.TryGetValue(type, out var row) && row.InheritsFrom is { Length: > 0 } parent
                ? QuestObjectTypes.Find(parent) : null);
        return info is not null && QuestObject.CanSerialize(info.Components, States.GetValueOrDefault(type) is not null) ? info : null;
    }

    /// <summary>The players in this connection's instance (it first), as the run's messages go to each.</summary>
    public static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == player.Instance).Distinct();

    // ---- the plan ----

    /// <summary>The run's nodes and objects: the rooms' own chests (Level.RoomObjects), then per cell (not Kishara's Rest,
    /// whose room places no packs either) DeepwaterRooms <c>__col10..__col11</c> Sulphur nodes, scattered objects, and any
    /// guaranteed chart/border objects (Lost Messages, Golden Lanterns, Pirate's Lockers, Treasure Anchors, Izaro Altars,
    /// Torment Cages), plus planned strongbox and Essence monolith placements returned for <c>StartRun</c> to spawn.</summary>
    public static (List<MapStrongboxes.SpawnRequest> Strongboxes, List<MapEssences.MonolithRequest> Monoliths) Plan(
        Player player, DeepwaterRun run, Action<string> log)
    {
        var layout = player.Layout;
        int seed = AreaPopulation.PopulationSeed(layout.Hash, player.Seed);
        var random = new Random(seed ^ 0x44574c54);
        // The corpses' poses come from their own sequence, so the placement above stays the same. G: uniform over the
        // .ot's eight set_animated brackets; GGG's distribution is unknown.
        var poses = new Random(seed ^ 0x504f5345);
        var start = run.Board.Cells[0].Point("bathysphere") ?? run.Board.Cells[0].Point("default") ?? (layout.SpawnX, layout.SpawnY);
        int fallbackFound = player.Instance?.KeptAreaStats?.GetValueOrDefault(ResourceFound) is int f ? f : 0;
        bool hardMode = LeagueFor(player).HardMode;
        var taken = new List<(int X, int Y)>();
        var things = new List<Thing>();
        var strongboxes = new List<MapStrongboxes.SpawnRequest>();
        var monoliths = new List<MapEssences.MonolithRequest>();
        var notes = new List<string>();

        foreach (var o in layout.Level?.RoomObjects ?? [])
        {
            Kind kind;
            if (o.Type == RotmothersStashType) kind = Kind.RotmothersStash;
            else if (Scattered.FirstOrDefault(s => s.Type == o.Type) is { Type: not null } s && (!s.VoyageOnly || run.Voyage)) kind = s.Kind;
            else continue;
            if (run.Board.CellAt((o.X, o.Y)) is not { } cell || Info(o.Type) is null) continue;
            things.Add(new Thing { Kind = kind, Type = o.Type, X = o.X, Y = o.Y, Cell = cell.Index, Rotation = o.Rotation, Model = ModelOf(o.Type, random), FromRoom = true,
                Pose = IsCorpse(o.Type) ? poses.Next(Poses) : 0, Id = player.TakeObjectId() });
            taken.Add((o.X, o.Y));
        }
        if (things.Count > 0) notes.Add($"{things.Count} from rooms");

        var pool = Scattered.Where(s => (!s.VoyageOnly || run.Voyage) && Info(s.Type) is not null)
            .Select(s => (s.Kind, s.Type, Weight: ChestRows.TryGetValue(s.Type, out var row) && row.Weight > 0 ? row.Weight : Setting(s.Fallback)))
            .Where(s => s.Weight > 0).ToList();
        var nodes = Nodes.Where(n => Info(n.Type) is not null).Select(n => (n.Type, Share: Setting(n.Share))).Where(n => n.Share > 0).ToList();
        var anchorTypes = TreasureAnchorChests
            .Select(t => hardMode && t == Chests + "DeepwaterChestScarabs" ? Chests + "DeepwaterChestScarabsHardMode" : t)
            .Where(t => Info(t) is not null).ToArray();
        string? izaroType = Info(IzaroAltarType) is not null ? IzaroAltarType : Info(IzaroChestFallback) is not null ? IzaroChestFallback : null;
        string? cageType = Info(TormentCageType) is not null ? TormentCageType : null;

        int PlaceFixed(DeepwaterBoard.Cell cell, Kind kind, string? type, int count)
        {
            if (count <= 0 || type is null || Info(type) is not { } info) return 0;
            int clearance = Math.Clamp(info.Size, 1, 3);
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                if (Spot(layout, cell, start, taken, ThingsApart, clearance, random) is not { } at) continue;
                things.Add(new Thing
                {
                    Kind = kind, Type = type, X = at.X, Y = at.Y, Cell = cell.Index, Model = ModelOf(type, random),
                    Rotation = (float)(random.NextDouble() * Math.Tau), Id = player.TakeObjectId(),
                });
                taken.Add(at);
                placed++;
            }
            return placed;
        }

        foreach (var cell in run.Board.Cells)
        {
            if (!RoomRanges.TryGetValue(cell.Room, out var range) || range.Packs <= 0 || range.Max <= 0) continue;
            var cellStats = run.Plan?.Effective(cell.Index);
            int cellFound = cellStats is not null ? cellStats.GetValueOrDefault(ResourceFound) : fallbackFound;
            int nodeCount = random.Next(range.Min, range.Max + 1);
            int objectCount = random.Next(range.Min, range.Max + 1) + DeepwaterUpgrades.ExtraScatteredObjects(player, random);
            int placedNodes = 0, placedObjects = 0;
            for (int i = 0; i < nodeCount && nodes.Count > 0; i++)
            {
                if (Spot(layout, cell, start, taken, NodesApart, 1, random) is not { } at) continue;
                string type = Pick(nodes.Select(n => (n.Type, n.Share)).ToList(), random);
                int multiplier = States.GetValueOrDefault(type) is { } st && st.IndexOf("base_resource_multiplier") is int m and >= 0 ? st.SharedStates[m] : 100;
                things.Add(new Thing { Kind = Kind.Node, Type = type, X = at.X, Y = at.Y, Cell = cell.Index, Sulphur = Amount(multiplier, run.Level, cellFound),
                    Pose = poses.Next(Poses), Id = player.TakeObjectId() });
                taken.Add(at);
                placedNodes++;
            }
            for (int i = 0; i < objectCount && pool.Count > 0; i++)
            {
                var (kind, type, _) = pool[PickIndex(pool.Select(p => p.Weight).ToList(), random)];
                int clearance = Math.Clamp(Info(type)?.Size ?? 1, 1, 3);
                if (Spot(layout, cell, start, taken, ThingsApart, clearance, random) is not { } at) continue;
                things.Add(new Thing { Kind = kind, Type = type, X = at.X, Y = at.Y, Cell = cell.Index, Model = ModelOf(type, random),
                    Rotation = (float)(random.NextDouble() * Math.Tau), Id = player.TakeObjectId() });
                taken.Add(at);
                placedObjects++;
            }
            if (cellStats is not null)
            {
                placedObjects += PlaceFixed(cell, Kind.Bottle, Chests + "BottledItemChest", Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartExtraLostMessages), 0, 10));
                placedObjects += PlaceFixed(cell, Kind.GoldenLantern, Objects + "DeepwaterGoldenLantern", Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartSpawnGoldenLanterns), 0, 10));
                placedObjects += PlaceFixed(cell, Kind.PiratesLocker, Chests + "RandomDucatChest", Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartXRandomDucatChests), 0, 10));
                int anchors = Math.Clamp(hardMode
                    ? cellStats.GetValueOrDefault(Stat.ChartContainsXAdditionalTreasureAnchorsHardMode)
                    : cellStats.GetValueOrDefault(Stat.ChartContainsXAdditionalTreasureAnchors), 0, 10);
                for (int i = 0; i < anchors && anchorTypes.Length > 0; i++)
                    placedObjects += PlaceFixed(cell, Kind.TreasureAnchor, anchorTypes[random.Next(anchorTypes.Length)], 1);
                placedObjects += PlaceFixed(cell, Kind.IzaroAltar, izaroType, Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartExtraIzaroObject), 0, 10));
                placedObjects += PlaceFixed(cell, Kind.TormentCage, cageType, Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartExtraTormentCages), 0, 10));

                // Strongboxes & Essence Monoliths: placed through Core/Game's native MapStrongboxes & MapEssences systems.
                var cellMapMods = new MapMods(cellStats);
                void QueueBoxes(StrongboxCatalog.Contents? kind, int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (Spot(layout, cell, start, taken, ThingsApart, 2, random) is not { } at) continue;
                        taken.Add(at);
                        strongboxes.Add(new MapStrongboxes.SpawnRequest(at.X, at.Y, kind, run.Level, cellMapMods, cell.BiomeArea));
                    }
                }
                QueueBoxes(null, Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartExtraStrongboxes), 0, 10));
                QueueBoxes(StrongboxCatalog.Contents.Currency, Math.Clamp(hardMode
                    ? cellStats.GetValueOrDefault(Stat.ChartExtraArcanistStrongboxesHardMode)
                    : cellStats.GetValueOrDefault(Stat.ChartExtraArcanistStrongboxes), 0, 10));
                QueueBoxes(StrongboxCatalog.Contents.Card, Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartExtraDivinerStrongboxes), 0, 10));
                QueueBoxes(StrongboxCatalog.Contents.Scarab, Math.Clamp(hardMode
                    ? cellStats.GetValueOrDefault(Stat.ChartExtraOperativeStrongboxesHardMode)
                    : cellStats.GetValueOrDefault(Stat.ChartExtraOperativeStrongboxes), 0, 10));

                int monolithCount = Math.Clamp(cellStats.GetValueOrDefault(Stat.ChartExtraMonoliths), 0, 10);
                for (int i = 0; i < monolithCount; i++)
                {
                    if (DeepwaterMonsters.PickVariety(cell.BiomeArea, run.Level, random) is not { } varietyId) break;
                    if (Spot(layout, cell, start, taken, ThingsApart, 3, random) is not { } at) continue;
                    taken.Add(at);
                    monoliths.Add(new MapEssences.MonolithRequest(at.X, at.Y, varietyId, run.Level, cellMapMods));
                }
            }
            notes.Add($"cell {cell.Index}: {placedNodes}/{nodeCount} nodes (found +{cellFound}%), {placedObjects} objects");
        }
        lock (run.Gate)
        {
            run.Things.AddRange(things);
            run.SulphurCap = things.Where(t => t.Kind == Kind.Node).Sum(t => t.Sulphur);
        }
        log($"Allflame loot: {things.Count(t => t.Kind == Kind.Node)} Sulphur nodes ({run.SulphurCap} Sulphur at level {run.Level}, balance {BalanceAt(run.Level)}), "
            + string.Join(", ", things.Where(t => t.Kind != Kind.Node).GroupBy(t => t.Kind).Select(g => $"{g.Count()} {g.Key}"))
            + (strongboxes.Count > 0 ? $", {strongboxes.Count} strongboxes" : "")
            + (monoliths.Count > 0 ? $", {monoliths.Count} monoliths" : "")
            + "; " + string.Join("; ", notes));
        return (strongboxes, monoliths);
    }

    // A Chests row's model when the type has none of its own (Gold Pile's four, Sunken Loot's two, Rotmother's Stash).
    static string? ModelOf(string type, Random random) =>
        Info(type) is { AnimatedModel: null } && ChestRows.TryGetValue(type, out var row) && row.Models.Length > 0
            ? row.Models[random.Next(row.Models.Length)] : null;

    static string Pick(List<(string Type, int Weight)> pool, Random random) => pool[PickIndex([.. pool.Select(p => p.Weight)], random)].Type;

    static int PickIndex(List<int> weights, Random random)
    {
        int roll = random.Next(Math.Max(1, weights.Sum()));
        for (int i = 0; i < weights.Count; i++) if ((roll -= weights[i]) < 0) return i;
        return weights.Count - 1;
    }

    static (int X, int Y)? Spot(AreaLayout layout, DeepwaterBoard.Cell cell, (int X, int Y) start, List<(int X, int Y)> taken, int apart,
        int clearance, Random random)
    {
        for (int t = 0; t < Tries; t++)
        {
            int x = random.Next(cell.X0, cell.X1), y = random.Next(cell.Y0, cell.Y1);
            if (!layout.WalkableFor(x, y, clearance) || Near((x, y), start, StartClear) || taken.Any(c => Near((x, y), c, apart))) continue;
            return (x, y);
        }
        return null;
    }

    static bool Near((int X, int Y) a, (int X, int Y) b, double distance)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy < distance * distance;
    }

    // ---- the objects ----

    /// <summary>The Sulphur corpses' poses: <c>set_animated_0..7</c> in <c>ResourceChest*.ot</c>.</summary>
    const int Poses = 8;

    /// <summary>A Sulphur corpse (ResourceChestSmall/Base/Large/Huge), whose script reads <c>this.RandomNumber</c>.</summary>
    static bool IsCorpse(string type) => type.StartsWith(Objects + "ResourceChest", StringComparison.Ordinal);

    static byte[]? Blob(Thing thing, QuestObjectTypes.Info info)
    {
        var states = States.GetValueOrDefault(thing.Type);
        if (states is not null)
            states = thing.Kind == Kind.Node
                ? states.Set("collected", thing.Done ? 1 : 0)
                // located 0 puts the object on the lantern pointer's list (globals.deepwater_poi); Soothing Coral is not
                // pointed at (3.29.0b).
                : states.Set("activated", thing.Done ? 1 : 0).Set("located", thing.Done || thing.Kind == Kind.SoothingCoral ? 1 : 0)
                    .Set("collected", thing.Done ? 1 : 0);
        var obj = new QuestObject(thing.X, thing.Y, info.Components, thing.Rotation, null, thing.Model,
            TransitionState: (byte)(thing.Done ? info.States : 1), Opened: thing.Done) { StateMachine = states };
        if (IsCorpse(thing.Type))
            obj = obj with { IntVariables = new Dictionary<uint, int> { [DeepwaterConstants.RandomNumberVariable] = thing.Pose } };
        return obj.Blob();
    }

    static async Task Create(Player player, Thing thing, uint id)
    {
        if (Info(thing.Type) is not { } info || Blob(thing, info) is not { } blob) return;
        var modPlayer = PlayerOf(player);
        if (!await global::Game.SendWorldCreate(modPlayer, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(thing.Type), blob),
            $"0x02bb Allflame {thing.Kind} 0x{id:x} {thing.Type.Split('/')[^1]} at ({thing.X}, {thing.Y}){(thing.Done ? " (used)" : "")}")) return;
        await global::Game.SendWorldRecord(modPlayer, id, InstanceProtocol.EnterWakeZone(id));
        if (info.Blocking) player.SetBlocker(id, thing.X, thing.Y, Math.Max(1, info.Size));
    }

    // The Golden Lantern buff's instance on each connection.
    static readonly ConditionalWeakTable<Player, StrongBox<ushort>> GoldenBuffs = new();

    /// <summary>Creates the run's nodes and objects on this connection (as they are now), its Sulphur gauge (0x2c9 mode 1)
    /// and the character's Golden Lantern stacks.</summary>
    public static async Task Show(Player player, DeepwaterRun run)
    {
        Thing[] things;
        int stacks;
        lock (run.Gate)
        {
            things = [.. run.Things];
            stacks = run.GoldenLanterns.GetValueOrDefault(CharacterName(player));
        }
        foreach (var thing in things) await Create(player, thing, thing.Id);
        await SendGauge(player, run);
        GoldenBuffs.Remove(player);
        if (stacks > 0) await SendGoldenBuff(player, stacks);
    }

    static async Task SendGauge(Player player, DeepwaterRun run)
    {
        int collected, cap;
        lock (run.Gate) (collected, cap) = (run.Sulphur, run.SulphurCap);
        // M: (collected, the run's possible total); the client fills its gauge a/b in five steps.
        if (DeepwaterRuleset.Sulphur((uint)collected, (uint)Math.Max(cap, collected)) is { } gauge)
            await PlayerOf(player).Send(gauge, $"0x02c9 ruleset DeepwaterRules mode 1: Sulphur {collected}/{Math.Max(cap, collected)}");
    }

    /// <summary>Awards Dead Man's Sulphur when a Rare monster dies inside a cell carrying
    /// <c>chart_rare_monsters_drop_dead_mans_sulphur</c>.</summary>
    public static async Task AwardRareSulphur(IModuleContext module, Player player, DeepwaterRun run, int cell)
    {
        int found = run.CellRules is { } rules && (uint)cell < (uint)rules.Length ? rules[cell].SulphurFoundPct : 0;
        int amount = Amount(100, run.Level, found);
        if (amount <= 0) return;
        int total;
        lock (run.Gate)
        {
            if (run.Failed || run.Phase == DeepwaterRun.RunPhase.Ended) return;
            run.Sulphur += amount;
            total = run.Sulphur;
        }
        foreach (var member in Members(player)) await SendGauge(member, run);
        module.Log($"{CharacterName(player)}: rare monster dropped +{amount} Sulphur (run {total}/{Math.Max(run.SulphurCap, total)})");
    }

    // ---- Sulphur ----

    /// <summary>A lantern's light collects every uncollected node inside its full radius: the node's <c>collected</c> = 1,
    /// the lantern's flare, the run's Sulphur and the gauge, on every connection in the run. Called on each placement
    /// (and for the first lantern), never per tick.</summary>
    public static async Task Collect(IModuleContext module, Player player, DeepwaterRun run, DeepwaterRun.Lantern lantern)
    {
        List<Thing> due;
        int total;
        lock (run.Gate)
        {
            due = [.. run.Things.Where(t => t.Kind == Kind.Node && !t.Done
                && Near((t.X, t.Y), (lantern.X, lantern.Y), DeepwaterConstants.BaseLanternRadius + 1))];
            if (due.Count == 0) return;
            foreach (var t in due) t.Done = true;
            run.Flared.Add(lantern.Id);
            run.Sulphur += due.Sum(t => t.Sulphur);
            total = run.Sulphur;
        }
        foreach (var member in Members(player))
        {
            await DeepwaterLanterns.Flare(member, lantern);
            foreach (var node in due) await SendStates(member, node);
            await SendGauge(member, run);
        }
        module.Log($"{CharacterName(player)}: lantern {lantern.Order} at ({lantern.X}, {lantern.Y}) collected {due.Count} Sulphur nodes, +{due.Sum(t => t.Sulphur)} (run {total}/{run.SulphurCap})");
    }

    static async Task SendStates(Player player, Thing thing)
    {
        if (Info(thing.Type) is not { } info || States.GetValueOrDefault(thing.Type) is not { } states) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        states = thing.Kind == Kind.Node ? states.Set("collected", thing.Done ? 1 : 0)
            : states.Set("activated", thing.Done ? 1 : 0).Set("located", 1).Set("collected", thing.Done ? 1 : 0);
        await global::Game.SendWorldRecord(PlayerOf(player), thing.Id, InstanceEntities.StateMachineUpdate(thing.Id, (byte)index, states),
            $"0x02bc Allflame {thing.Kind} 0x{thing.Id:x} {(thing.Kind == Kind.Node ? "collected" : "activated, located")}");
        if (thing.Kind != Kind.Node && QuestObject.TransitionableIndex(info.Components) is int t and >= 0 && thing.Done)
            await global::Game.SendWorldRecord(PlayerOf(player), thing.Id, InstanceEntities.TransitionableUpdate(thing.Id, (byte)t, (byte)info.States),
                $"0x02bc Allflame {thing.Kind} 0x{thing.Id:x} Transitionable → {info.States}");
    }

    // ---- use ----

    /// <summary>C→S 0x24 on a chest or interactable of the run: refused (quietly) unless it is in a lantern's light, else
    /// the player walks up, it is used once (a chest is created again opened under a new id; an interactable's
    /// <c>activated</c> and <c>located</c> = 1 and its Transitionable's last state) and gives its reward.
    /// False when the object is not one of the run's.</summary>
    public static async Task<bool> Use(IModuleContext module, Player player, DeepwaterRun run, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        Thing? thing;
        lock (run.Gate) thing = run.Things.FirstOrDefault(t => t.Id == use.TargetId && t.Kind != Kind.Node);
        if (thing is null) return false;
        var modPlayer = PlayerOf(player);
        // A failed run's instance is dead: nothing in it opens any more.
        if (thing.Done || run.Failed || player.PlayerLife == 0 || player.Closed || player.LeavingArea || Info(thing.Type) is not { } info)
        {
            await EndAction(player);
            return true;
        }
        double now = AreaTime(player);
        if (!run.InLight((thing.X, thing.Y), now))
        {
            // ClientStrings 8447 DeepwaterErrorCannotInteractWhileDrowning; no message that shows it is known: refused quietly.
            module.Log($"{modPlayer.Character} <= 0x0024 Allflame {thing.Kind} 0x{thing.Id:x} at ({thing.X}, {thing.Y}) refused: not within Lanternlight");
            await EndAction(player);
            return true;
        }
        int reach = Math.Max(1, info.Size);
        if (!TryBeginObjectWalk(player, requestedAt, thing.X, thing.Y, reach, out var walk)) return true;
        var (action, start, here, _, arrive) = walk;
        if (!use.Started)
            await global::Game.SendWorldRecord(modPlayer, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, thing.X, thing.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: thing.Id));
        if (arrive > start) await AreaDelay(player, arrive - start);
        try
        {
            double arrivedAt = AreaTime(player);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea || run.Failed
                || !run.InLight((thing.X, thing.Y), arrivedAt)
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(player.Walk.Position(arrivedAt), (thing.X, thing.Y), reach)) return true;
            uint was;
            lock (run.Gate)
            {
                if (thing.Done) return true;
                thing.Done = true;
                was = thing.Id;
                if (info.Components.Contains("Chest")) thing.Id = player.TakeObjectId();
            }
            module.Log($"{modPlayer.Character} <= 0x0024 Allflame {thing.Kind} 0x{was:x} {thing.Type.Split('/')[^1]} at ({thing.X}, {thing.Y}) used");
            foreach (var member in Members(player))
            {
                if (was != thing.Id)
                {
                    member.RemoveBlocker(was);
                    await global::Game.SendWorldRemoval(PlayerOf(member), was);
                    await Create(member, thing, thing.Id);
                }
                else await SendStates(member, thing);
            }
            await Reward(module, player, run, thing);
        }
        finally { await EndAction(player, action); }
        return true;
    }

    /// <summary>Ends the player's interaction (0x1ad), after a refusal or a use.</summary>
    public static Task EndAction(Player player, int? action = null) =>
        global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null,
            still: action is int a ? () => PlayerActionCurrent(player, a) : null);

    static async Task Reward(IModuleContext module, Player player, DeepwaterRun run, Thing thing)
    {
        var catalog = ItemsFor(player);
        var rng = AreaRng(player);
        int level = Math.Clamp(run.Level, 1, 100);
        switch (thing.Kind)
        {
            case Kind.SunkenLoot:
            {
                var drops = RollSunkenCurrency(catalog, level, rng);
                // G: special-room Sunken Loot Ducat drops (Anchorfield -> Kishara, Lost Ruins -> Tzamoto; other rooms small chance for Changeling/Telesia).
                int room = thing.Cell >= 0 && thing.Cell < run.Board.Cells.Count ? run.Board.Cells[thing.Cell].Room : -1;
                string? bonusDucat = room switch
                {
                    19 or 20 when rng.Next(100) < SettingOr("deepwater_room_ducat_chance", 15) => DeepwaterDucats.Kishara,
                    48 or 49 when rng.Next(100) < SettingOr("deepwater_room_ducat_chance", 15) => DeepwaterDucats.Tzamoto,
                    _ when rng.Next(100) < SettingOr("deepwater_sunken_ducat_chance", 4) =>
                        rng.Next(2) == 0 ? DeepwaterDucats.Changeling : DeepwaterDucats.Telesia,
                    _ => null,
                };
                if (bonusDucat is not null && catalog.Find(bonusDucat) is { Supported: true } ducatType)
                    drops.Add(new(ItemCatalog.Create(ducatType) with { StackSize = 1, ItemLevel = (byte)level }, ducatType));
                if (DeepwaterUpgrades.TryChestPiece1(module, player, run.Voyage, IsSpecialRoom(room), level) is { } piece1)
                    drops.Add(piece1);
                await Drop(player, thing, ItemDrops.Stacked(drops));
                break;
            }
            case Kind.PiratesLocker:
            {
                var drops = new List<ItemDrops.Drop>(2);
                string id = DeepwaterDucats.AllDucats[rng.Next(DeepwaterDucats.AllDucats.Length)];
                if (catalog.Find(id) is { Supported: true } ducatType)
                    drops.Add(new(ItemCatalog.Create(ducatType) with { StackSize = 1, ItemLevel = (byte)level }, ducatType));
                int room = thing.Cell >= 0 && thing.Cell < run.Board.Cells.Count ? run.Board.Cells[thing.Cell].Room : -1;
                if (DeepwaterUpgrades.TryChestPiece1(module, player, run.Voyage, IsSpecialRoom(room), level) is { } piece1)
                    drops.Add(piece1);
                if (drops.Count > 0) await Drop(player, thing, drops);
                break;
            }
            case Kind.DrownedOffering:
            {
                if (await DeepwaterCursedDucats.DropFromOffering(module, player, level) is { } cursedDrop)
                    await Drop(player, thing, [cursedDrop]);
                else
                {
                    // Already unlocked Ducat of the Undead Sea: drop 1 random crafting Ducat instead (G).
                    string id = DeepwaterDucats.AllDucats[rng.Next(DeepwaterDucats.AllDucats.Length)];
                    if (catalog.Find(id) is { Supported: true } ducatType)
                        await Drop(player, thing, [new(ItemCatalog.Create(ducatType) with { StackSize = 1, ItemLevel = (byte)level }, ducatType)]);
                }
                break;
            }
            case Kind.RotmothersStash:
            {
                var drops = RollSunkenCurrency(catalog, level, rng);
                if (catalog.Find(DeepwaterDucats.Rotmother) is { Supported: true } rotType)
                    drops.Add(new(ItemCatalog.Create(rotType) with { StackSize = 1, ItemLevel = (byte)level }, rotType));
                await Drop(player, thing, ItemDrops.Stacked(drops));
                break;
            }
            case Kind.GoldPile or Kind.Clam:
            {
                // G: level × 8-15 gold; a Treasure Clam half (its rare uniques are not built).
                int amount = level * rng.Next(8, 16) / (thing.Kind == Kind.Clam ? 2 : 1);
                if (catalog.Find(ItemDrops.GoldId) is { Supported: true } gold && amount > 0)
                    await Drop(player, thing, [new(ItemCatalog.Create(gold) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) }, gold)]);
                break;
            }
            case Kind.Bottle:
                // The bottle item as plain loot after the chest's item_drop_delay; opening it is a later step.
                // The use's action ends at once; the drop follows on its own.
                if (catalog.Find(BottleItem) is { Supported: true } bottle)
                {
                    var instance = player.Instance;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AreaDelay(player, BottleDelay);
                            if (!player.Closed && !player.LeavingArea && player.Instance == instance)
                                await Drop(player, thing, [new(ItemCatalog.Create(bottle) with { ItemLevel = (byte)level }, bottle)]);
                        }
                        catch (Exception ex) { module.Log($"{CharacterName(player)}: the bottle's drop failed: {ex.Message}"); }
                    });
                }
                else module.Log($"{CharacterName(player)}: {BottleItem} is not an item the server writes; the bottle drops nothing");
                break;
            case Kind.GoldenLantern:
            {
                int stacks;
                lock (run.Gate) stacks = run.GoldenLanterns[CharacterName(player)] = run.GoldenLanterns.GetValueOrDefault(CharacterName(player)) + 1;
                await SendGoldenBuff(player, stacks);
                break;
            }
            case Kind.SoothingCoral:
                await Refill(player);
                break;
            case Kind.Kindling:
                lock (run.Gate) run.Total += 3;
                foreach (var member in Members(player)) await DeepwaterLanterns.SendCount(member, run);
                break;
            case Kind.TormentCage:
            {
                var spots = new List<(int X, int Y)>(3);
                for (int i = 0; i < 3; i++)
                {
                    double angle = Math.Tau * i / 3.0 + rng.NextDouble() * 0.5;
                    spots.Add(player.Layout.Near((thing.X + (int)Math.Round(Math.Cos(angle) * 8), thing.Y + (int)Math.Round(Math.Sin(angle) * 8))));
                }
                int released = await MapTorment.SpawnSpiritsAt(player, spots, (byte)level);
                module.Log($"{CharacterName(player)}: Torment Cage 0x{thing.Id:x} released {released} Tormented Spirits");
                break;
            }
            case Kind.IzaroAltar:
            {
                var drops = RollSunkenCurrency(catalog, level, rng);
                if (catalog.Find(OfferingToTheGoddessId) is { Supported: true } offering)
                    drops.Add(new(ItemCatalog.Create(offering) with { StackSize = 1, ItemLevel = (byte)level }, offering));
                if (DeepwaterChartStats.RollSupportGem(level, rng, LeagueFor(player).Rules) is { } gem
                    && catalog.ByHash(gem.TypeHash) is { } gemType)
                    drops.Add(new(gem with { Quality = (byte)rng.Next(10, 21) }, gemType));
                await Drop(player, thing, ItemDrops.Stacked(drops));
                break;
            }
            case Kind.TreasureAnchor:
            {
                var drops = RollTreasureAnchorLoot(player, catalog, thing.Type, level);
                if (drops.Count > 0) await Drop(player, thing, ItemDrops.Stacked(drops));
                break;
            }
        }
    }

    static List<ItemDrops.Drop> RollTreasureAnchorLoot(Player player, ItemCatalog catalog, string chestType, int level)
    {
        var rng = AreaRng(player);
        var drops = new List<ItemDrops.Drop>();
        if (chestType.EndsWith("StackedDecks", StringComparison.Ordinal))
        {
            if (catalog.Find(DeepwaterChartStats.StackedDeckId) is { Supported: true } deck)
                drops.Add(new(ItemCatalog.Create(deck) with { StackSize = (ushort)rng.Next(2, 5), ItemLevel = (byte)level }, deck));
            return drops;
        }
        if (chestType.Contains("Scarab", StringComparison.Ordinal))
        {
            var roller = DropsFor(player);
            var mods = MapModsOf(player);
            for (int i = rng.Next(1, 3); i > 0; i--)
                if (roller.Scarab(rng, level, mods) is { } scarab) drops.Add(scarab);
            return drops;
        }
        if (chestType.Contains("AnchorUnique", StringComparison.Ordinal))
        {
            bool Matches(ItemCatalog.BaseType t) => chestType.EndsWith("Armour", StringComparison.Ordinal)
                ? t.Class is "Body Armour" or "Helmet" or "Gloves" or "Boots" or "Shield"
                : chestType.EndsWith("Weapon", StringComparison.Ordinal)
                    ? t.Components.Contains("Weapon")
                    : t.Class is "Ring" or "Amulet" or "Belt";
            var uniques = UniquesFor(player);
            var weightOf = DropsFor(player).UniqueWeight;
            var candidates = new List<(UniqueCatalog.Definition Def, double Weight)>();
            double total = 0;
            foreach (var d in uniques.All)
            {
                if (d.EncounterOnly) continue;
                if (catalog.Find(d.BaseTypeId) is not { Supported: true } bt || bt.DropLevel > level || !Matches(bt)) continue;
                double w = weightOf(d.Name);
                if (!double.IsFinite(w) || w <= 0) continue;
                candidates.Add((d, w));
                total += w;
            }
            if (candidates.Count > 0)
            {
                double pick = rng.NextDouble() * total;
                var chosen = candidates[^1].Def;
                foreach (var (d, w) in candidates) { if ((pick -= w) < 0) { chosen = d; break; } }
                if (uniques.Create(chosen with { ItemLevel = (byte)level }, rng) is { } item
                    && catalog.ByHash(item.TypeHash) is { } uType)
                    drops.Add(new(item with { Identified = false }, uType));
            }
            drops.AddRange(RollSunkenCurrency(catalog, level, rng));
            return drops;
        }
        if (chestType.EndsWith("RareJewellery", StringComparison.Ordinal))
        {
            drops.AddRange(DropsFor(player).Container(level, rng.Next(2, 4), 100, rng, t => t.Class is "Ring" or "Amulet" or "Belt"));
            drops.AddRange(RollSunkenCurrency(catalog, level, rng));
            return drops;
        }
        // CurrencyTreasureChestOpulent / fallback: richer currency + small Ducat chance.
        drops.AddRange(RollSunkenCurrency(catalog, level, rng));
        drops.AddRange(RollSunkenCurrency(catalog, level, rng));
        if (rng.Next(100) < SettingOr("deepwater_sunken_ducat_chance", 8))
        {
            string id = DeepwaterDucats.AllDucats[rng.Next(DeepwaterDucats.AllDucats.Length)];
            if (catalog.Find(id) is { Supported: true } ducatType)
                drops.Add(new(ItemCatalog.Create(ducatType) with { StackSize = 1, ItemLevel = (byte)level }, ducatType));
        }
        return drops;
    }

    // G: 3-6 currency items by the server's currency weights (ItemDrops.Currency) at the run's level.
    static List<ItemDrops.Drop> RollSunkenCurrency(ItemCatalog catalog, int level, Random rng)
    {
        var pool = ItemDrops.Currency.Select(c => (Type: catalog.Find(c.Id), c.Weight))
            .Where(c => c.Type is { Supported: true } && c.Type.DropLevel <= level).ToList();
        var drops = new List<ItemDrops.Drop>();
        for (int i = rng.Next(3, 7); i > 0 && pool.Count > 0; i--)
        {
            double roll = rng.NextDouble() * pool.Sum(p => p.Weight);
            var pick = pool[^1];
            foreach (var p in pool) if ((roll -= p.Weight) < 0) { pick = p; break; }
            drops.Add(new(ItemCatalog.Create(pick.Type!) with { StackSize = 1, ItemLevel = (byte)level }, pick.Type!));
        }
        return drops;
    }

    static Task Drop(Player player, Thing thing, IReadOnlyList<ItemDrops.Drop> drops) =>
        Drop(player, thing.X, thing.Y, Info(thing.Type)?.Size ?? 1, $"Allflame {thing.Kind} 0x{thing.Id:x}", drops);

    /// <summary>Items on the ground in a ring around an object of this size, as just dropped (nothing when the area allows
    /// no loot).</summary>
    internal static async Task Drop(Player player, int x, int y, int size, string label, IReadOnlyList<ItemDrops.Drop> drops)
    {
        if (!GameEvents.AllowsLoot(player) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var rng = AreaRng(player);
        int ring = Math.Max(2, size / 2 + 2);
        for (int i = 0; i < drops.Count; i++)
        {
            double angle = i * 2.399963229728653 + rng.NextDouble();
            var at = player.Layout.Near((x + (int)Math.Round(Math.Cos(angle) * ring), y + (int)Math.Round(Math.Sin(angle) * ring)));
            var ground = new WorldItemObject(at.X, at.Y, drops[i].Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, $"{label} drops {drops[i].Type.Name} ×{drops[i].Item.StackSize}", justDropped: true);
        }
    }

    /// <summary>Golden Lantern's Glow (BuffDefinitions 2798) with its stacks, one per Golden Lantern used, for the rest of
    /// the run: the same instance again with the new count.</summary>
    static async Task SendGoldenBuff(Player player, int stacks)
    {
        var box = GoldenBuffs.GetValue(player, p =>
        {
            ushort id = p.NextBuff++;
            return new StrongBox<ushort>(id == 0 ? p.NextBuff++ : id);
        });
        await global::Game.SendWorldRecord(PlayerOf(player), player.Id,
            InstanceEntities.AddBuff(player.Id, box.Value, DeepwaterConstants.GoldenLanternBuff, stacks: (ushort)Math.Clamp(stacks, 1, ushort.MaxValue)),
            $"0x01c1 Golden Lantern's Glow × {stacks}");
    }

    // Soothing Coral: life, mana and energy shield full, and the flasks' charges.
    static async Task Refill(Player player)
    {
        if (player.PlayerLife == 0) return;
        player.PlayerLife = Math.Max(player.PlayerLife, LifeCap(player));
        player.EnergyShield.SetMaximum(player.Pools.EnergyShield, refill: true);
        player.PlayerMana = Math.Max(player.PlayerMana, (uint)UnreservedMana(player));
        await global::Game.SendWorldRecord(player, player.Id, PlayerLifeUpdate(player), $"0x01b1 Soothing Coral: life {player.PlayerLife}");
        await SendPlayerMana(player, $"0x01b1 Soothing Coral: mana {player.PlayerMana}");
        await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.LifeUpdate(player.Id, player.EnergyShield.Current, pool: 2),
            $"0x01b1 Soothing Coral: energy shield {player.EnergyShield.Current}");
        await GainFlaskCharges(player, int.MaxValue, "Soothing Coral");
    }

    /// <summary>Precomputes the special-room rare-monster Ducat target and drop chance (%) per board cell (built once at
    /// <c>StartRun</c>, O(1) lookup and 0 allocations on the per-kill hot path).</summary>
    public static (string?[]? Ducats, int[]? Chances) RoomRareDucats(DeepwaterBoard board)
    {
        int n = board.Cells.Count;
        string?[]? ducats = null;
        int[]? chances = null;
        int roomChance = SettingOr("deepwater_room_ducat_chance", 15);
        for (int i = 0; i < n; i++)
        {
            var (ducat, chance) = board.Cells[i].Room switch
            {
                61 or 62 => (DeepwaterDucats.Brinehook, 15),          // Brine King's Domain (room stat 23289 = 15%)
                42 or 43 => (DeepwaterDucats.Merrick, roomChance),    // Pelagic Abyss (G)
                44 or 45 => (DeepwaterDucats.Ukatoa, roomChance),     // Eldritch Depths (G)
                21 or 22 or 46 or 47 => (DeepwaterDucats.Genteel, roomChance), // Infested Bathyspheres / Sea Pillars (G)
                63 or 64 => (DeepwaterDucats.Cyaxan, roomChance),     // Diving Shoals (G)
                65 or 66 => (DeepwaterDucats.Katakohi, roomChance),   // Sunken Totems (G)
                _ => ((string?)null, 0),
            };
            if (ducat is null || chance <= 0) continue;
            ducats ??= new string?[n];
            chances ??= new int[n];
            ducats[i] = ducat;
            chances[i] = chance;
        }
        return (ducats, chances);
    }

    // ---- drops ----

    /// <summary>GameEvents.Drops in a run: the league's monsters drop map-related items at −50 %
    /// (<c>map_related_item_drop_chance_+%_final_from_league</c> −50, DeepwaterBase.ot), Cursed Ducat IIQ/IIR multipliers
    /// apply to all kills, a Voyage cell multiplies by its own quantity and rarity, special-room rare monsters roll their
    /// targeted Ducat, and the killer's Golden Lantern stacks add +10 % quantity and +30 % rarity each (Ruthless +3 % / +10 %;
    /// the wiki), applied as multipliers (G).</summary>
    public static void Drops(DropRoll roll, DeepwaterRun run)
    {
        if (roll.Monster.VarietyId.StartsWith(LeagueMonsters, StringComparison.Ordinal)) roll.MapRelated *= 0.5;
        DeepwaterCursedDucats.ApplyDropFactors(roll, run.CursedQuantityFactor, run.CursedRarityFactor);
        bool isRare = roll.Monster.Rarity >= MonsterRarity.Rare;
        var rng = AreaRng(roll.Player);
        // Compute cell index once for Voyage cell multipliers, special-room rare Ducats, and per-cell chart/border rules (0 allocs per kill).
        int cell = (run.CellQuantity is not null || run.HasAnyCellDrops || (isRare && run.CellRareDucat is not null))
            ? run.CellIndex(roll.Monster.X, roll.Monster.Y) : -1;
        if (run.CellQuantity is { } cellQuantity && run.CellRarity is { } cellRarity && (uint)cell < (uint)cellQuantity.Length)
        {
            roll.Quantity *= cellQuantity[cell];
            roll.Rarity *= cellRarity[cell];
        }
        if (isRare && run.CellRareDucat is { } cellDucats && run.CellRareDucatChance is { } cellChances
            && (uint)cell < (uint)cellDucats.Length && cellDucats[cell] is { } ducatId && cellChances[cell] > 0
            && rng.Next(100) < cellChances[cell])
        {
            roll.Items.Add(ducatId);
        }
        if (run.HasAnyCellDrops && run.CellRules is { } rules && (uint)cell < (uint)rules.Length)
        {
            ref readonly var cr = ref rules[cell];
            if (cr.ExtraScarabFactor > 1.0)
            {
                double scarabChance = Math.Clamp(SystemSettings.Default.Get("drop_scarab_chance_pct"), 0, 100) / 100.0 * (cr.ExtraScarabFactor - 1.0);
                if (rng.NextDouble() < scarabChance
                    && DropsFor(roll.Player).Scarab(rng, roll.Monster.Level, MapModsOf(roll.Player)) is { } extraScarab)
                    roll.Made.Add(extraScarab.Item);
            }
            if (cr.ExtraCurrencyFactor > 1.0)
            {
                double expected = global::Game.CurrencyDropExpected(roll.Monster.Rarity) * (cr.ExtraCurrencyFactor - 1.0);
                int whole = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
                var roller = DropsFor(roll.Player);
                for (int i = 0; i < whole; i++)
                    if (roller.CurrencyDrop(roll.Monster.Level, rng) is { } extraCur)
                        roll.Made.Add(extraCur.Item);
            }
            if (isRare)
            {
                if (cr.RareCurrencyDrops is { Length: > 0 } rareDrops)
                {
                    for (int i = 0; i < rareDrops.Length; i++)
                        roll.Stacks.Add(rareDrops[i]);
                }
                if (cr.RareExtraScarab > 0)
                {
                    var roller = DropsFor(roll.Player);
                    var mods = MapModsOf(roll.Player);
                    for (int i = 0; i < cr.RareExtraScarab; i++)
                        if (roller.Scarab(rng, roll.Monster.Level, mods) is { } rareScarab)
                            roll.Made.Add(rareScarab.Item);
                }
                if (cr.RareSupportGemChance > 0 && rng.Next(100) < cr.RareSupportGemChance
                    && DeepwaterChartStats.RollSupportGem(roll.Monster.Level, rng, LeagueFor(roll.Player).Rules) is { } supportGem)
                {
                    roll.Made.Add(supportGem);
                }
            }
            if (cr.HasTransforms)
            {
                var capturedRules = cr;
                var catalog = ItemsFor(roll.Player);
                var uniques = UniquesFor(roll.Player);
                var uniqueWeight = DropsFor(roll.Player).UniqueWeight;
                int monsterLevel = roll.Monster.Level;
                var monsterRarity = roll.Monster.Rarity;
                roll.AddTransform(drops => DeepwaterChartStats.ApplyDropTransforms(
                    drops, catalog, uniques, in capturedRules, monsterLevel, monsterRarity, rng, uniqueWeight));
            }
        }
        int stacks;
        lock (run.Gate) stacks = run.GoldenLanterns.GetValueOrDefault(CharacterName(roll.Player));
        if (stacks == 0) return;
        var (rarity, quantity) = LeagueFor(roll.Player).HardMode ? (10, 3) : (30, 10);
        roll.Quantity *= 1 + quantity * stacks / 100.0;
        roll.Rarity *= 1 + rarity * stacks / 100.0;
    }

    /// <summary>The run's loot for /state.</summary>
    public static System.Text.Json.Nodes.JsonObject State(DeepwaterRun run)
    {
        lock (run.Gate)
            return new()
            {
                ["sulphur"] = run.Sulphur, ["sulphurCap"] = run.SulphurCap,
                ["nodes"] = run.Things.Count(t => t.Kind == Kind.Node), ["nodesCollected"] = run.Things.Count(t => t.Kind == Kind.Node && t.Done),
                ["objects"] = new System.Text.Json.Nodes.JsonArray([.. run.Things.Where(t => t.Kind != Kind.Node).Select(t => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = t.Id, ["kind"] = t.Kind.ToString(), ["x"] = t.X, ["y"] = t.Y, ["cell"] = t.Cell, ["used"] = t.Done, ["fromRoom"] = t.FromRoom,
                })]),
                ["goldenLanterns"] = new System.Text.Json.Nodes.JsonObject([.. run.GoldenLanterns.Select(g => KeyValuePair.Create(g.Key, (System.Text.Json.Nodes.JsonNode?)g.Value))]),
            };
    }
}
