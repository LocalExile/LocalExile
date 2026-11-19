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
    public enum Kind { Node, SunkenLoot, GoldPile, Clam, Bottle, GoldenLantern, SoothingCoral, Kindling }

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
        public bool Done { get; set; }
    }

    const string Objects = "Metadata/Terrain/Leagues/Deepwater/Objects/";
    const string Chests = "Metadata/Chests/LeagueDeepwater/";
    const string LeagueMonsters = "Metadata/Monsters/LeagueDeepwater/";
    const string BottleItem = "Metadata/Items/Deepwater/DeepwaterBottledItem";
    const string ResourceFound = "map_deepwater_league_resource_found_+%";

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
        (Kind.GoldenLantern, Objects + "DeepwaterGoldenLantern", "deepwater_weight_golden_lantern", false),
        (Kind.SoothingCoral, Objects + "DeepwaterFlaskRefill", "deepwater_weight_soothing_coral", false),
        (Kind.Kindling, Objects + "DeepwaterLanternReplenishEncounter", "deepwater_weight_allflame_kindling", true),
    ];

    /// <summary>A world.db <c>system_settings</c> weight; 0 (none placed) without its row.</summary>
    static int Setting(string key) => SystemSettings.Default.Values.TryGetValue(key, out var v) && double.IsFinite(v) ? (int)Math.Clamp(v, 0, 10_000) : 0;

    // G: the spacing of the scattered objects and nodes, and the clear ring around the start Bathysphere.
    const int ThingsApart = 15, NodesApart = 12, StartClear = 20, Tries = 200;
    /// <summary>Message in a Bottle's Chest <c>item_drop_delay</c>.</summary>
    const double BottleDelay = 2.3;

    static readonly Lazy<Dictionary<string, StateMachineData?>> States = new(() =>
    {
        var shared = ObjectTypes.SharedStates();
        var types = Nodes.Select(n => n.Type).Concat(Scattered.Select(s => s.Type)).ToHashSet(StringComparer.Ordinal);
        return ObjectTypes.All().Where(t => types.Contains(t.Type)).ToDictionary(t => t.Type, t => ObjectTypes.StateMachine(t.Row, shared));
    });

    // Chests rows by Id: __col7 (read as a spawn weight, G) and AOFiles.
    static readonly Lazy<Dictionary<string, (int Weight, string[] Models)>> ChestRows = new(() => ClientTables.TableOrEmpty("Chests")
        .Where(r => r.Text("Id") is { } id && id.StartsWith(Chests, StringComparison.Ordinal))
        .ToDictionary(r => r.Text("Id")!, r => (r.IntOrNull("__col7") ?? 0, r.Texts("AOFiles").ToArray())));

    static readonly Lazy<Dictionary<int, (int Min, int Max, int Packs)>> RoomRanges = new(() => ClientTables.TableOrEmpty("DeepwaterRooms")
        .ToDictionary(r => r.Index, r => (r.IntOrNull("__col10") ?? 0, r.IntOrNull("__col11") ?? 0, r.IntOrNull("__col8") ?? 0)));

    static readonly Lazy<Dictionary<int, int>> Balance = new(() => ClientTables.TableOrEmpty("DeepwaterBalancePerLevel")
        .Where(r => r.IntOrNull("__col0") is not null).ToDictionary(r => r.Int("__col0"), r => r.IntOrNull("__col1") ?? 0));

    /// <summary>DeepwaterBalancePerLevel <c>__col1</c> at a level (−66 at 1, 0 at 67, +144 from 85), clamped to the table.</summary>
    static int BalanceAt(int level)
    {
        var table = Balance.Value;
        if (table.Count == 0) return 0;
        return table.GetValueOrDefault(Math.Clamp(level, table.Keys.Min(), table.Keys.Max()));
    }

    /// <summary>A node's Sulphur (G): <c>round(multiplier × (100 + balance(level)) / 100 × (100 + found%) / 100)</c>.</summary>
    static int Amount(int multiplier, int level, int found) =>
        Math.Max(0, (int)Math.Round(multiplier * (100 + BalanceAt(level)) / 100.0 * Math.Max(0, 100 + found) / 100.0));

    static QuestObjectTypes.Info? Info(string type) =>
        QuestObjectTypes.Find(type) is { } info && QuestObject.CanSerialize(info.Components, States.Value.GetValueOrDefault(type) is not null) ? info : null;

    /// <summary>The players in this connection's instance (it first), as the run's messages go to each.</summary>
    public static IEnumerable<ClientState> Members(ClientState conn) => conn.Instance.PlayersExcept(conn.PeerId)
        .Select(p => p.Owner).OfType<ClientState>().Prepend(conn)
        .Where(p => !p.Closed && !p.LeavingArea && p.Instance == conn.Instance).Distinct();

    // ---- the plan ----

    /// <summary>The run's nodes and objects: the rooms' own chests (Level.RoomObjects), then per cell (not Kishara's Rest,
    /// whose room places no packs either) DeepwaterRooms <c>__col10..__col11</c> Sulphur nodes and as many scattered
    /// objects (G: the column pair is read as both counts; 3.29.0b's "20-40 % more scattered objects"), on walkable
    /// cells away from each other and from the start Bathysphere. The run's Sulphur cap is the sum of its nodes.</summary>
    public static void Plan(ClientState conn, DeepwaterRun run, Action<string> log)
    {
        var layout = conn.Layout;
        var random = new Random(AreaPopulation.PopulationSeed(layout.Hash, conn.Seed) ^ 0x44574c54);
        var start = run.Board.Cells[0].Point("bathysphere") ?? run.Board.Cells[0].Point("default") ?? (layout.SpawnX, layout.SpawnY);
        int found = Stats.Key(ResourceFound) is uint key && conn.Instance?.KeptAreaStats?.GetValueOrDefault(key) is int f ? f : 0;
        var taken = new List<(int X, int Y)>();
        var things = new List<Thing>();
        var notes = new List<string>();

        foreach (var o in layout.Level?.RoomObjects ?? [])
        {
            if (Scattered.FirstOrDefault(s => s.Type == o.Type) is not { Type: not null } s || s.VoyageOnly && !run.Voyage
                || run.Board.CellAt((o.X, o.Y)) is not { } cell || Info(o.Type) is null) continue;
            things.Add(new Thing { Kind = s.Kind, Type = o.Type, X = o.X, Y = o.Y, Cell = cell.Index, Rotation = o.Rotation, Model = ModelOf(o.Type, random), FromRoom = true, Id = conn.TakeObjectId() });
            taken.Add((o.X, o.Y));
        }
        if (things.Count > 0) notes.Add($"{things.Count} from rooms");

        var pool = Scattered.Where(s => (!s.VoyageOnly || run.Voyage) && Info(s.Type) is not null)
            .Select(s => (s.Kind, s.Type, Weight: ChestRows.Value.TryGetValue(s.Type, out var row) && row.Weight > 0 ? row.Weight : Setting(s.Fallback)))
            .Where(s => s.Weight > 0).ToList();
        var nodes = Nodes.Where(n => Info(n.Type) is not null).Select(n => (n.Type, Share: Setting(n.Share))).Where(n => n.Share > 0).ToList();
        foreach (var cell in run.Board.Cells)
        {
            if (!RoomRanges.Value.TryGetValue(cell.Room, out var range) || range.Packs <= 0 || range.Max <= 0) continue;
            int nodeCount = random.Next(range.Min, range.Max + 1), objectCount = random.Next(range.Min, range.Max + 1);
            int placedNodes = 0, placedObjects = 0;
            for (int i = 0; i < nodeCount && nodes.Count > 0; i++)
            {
                if (Spot(layout, cell, start, taken, NodesApart, 1, random) is not { } at) continue;
                string type = Pick(nodes.Select(n => (n.Type, n.Share)).ToList(), random);
                int multiplier = States.Value.GetValueOrDefault(type) is { } st && st.IndexOf("base_resource_multiplier") is int m and >= 0 ? st.SharedStates[m] : 100;
                things.Add(new Thing { Kind = Kind.Node, Type = type, X = at.X, Y = at.Y, Cell = cell.Index, Sulphur = Amount(multiplier, run.Level, found), Id = conn.TakeObjectId() });
                taken.Add(at);
                placedNodes++;
            }
            for (int i = 0; i < objectCount && pool.Count > 0; i++)
            {
                var (kind, type, _) = pool[PickIndex(pool.Select(p => p.Weight).ToList(), random)];
                int clearance = Math.Clamp(Info(type)?.Size ?? 1, 1, 3);
                if (Spot(layout, cell, start, taken, ThingsApart, clearance, random) is not { } at) continue;
                things.Add(new Thing { Kind = kind, Type = type, X = at.X, Y = at.Y, Cell = cell.Index, Model = ModelOf(type, random),
                    Rotation = (float)(random.NextDouble() * Math.Tau), Id = conn.TakeObjectId() });
                taken.Add(at);
                placedObjects++;
            }
            notes.Add($"cell {cell.Index}: {placedNodes}/{nodeCount} nodes, {placedObjects}/{objectCount} objects");
        }
        lock (run.Gate)
        {
            run.Things.AddRange(things);
            run.SulphurCap = things.Where(t => t.Kind == Kind.Node).Sum(t => t.Sulphur);
        }
        log($"Allflame loot: {things.Count(t => t.Kind == Kind.Node)} Sulphur nodes ({run.SulphurCap} Sulphur at level {run.Level}, balance {BalanceAt(run.Level)}, found +{found}%), "
            + string.Join(", ", things.Where(t => t.Kind != Kind.Node).GroupBy(t => t.Kind).Select(g => $"{g.Count()} {g.Key}"))
            + "; " + string.Join("; ", notes));
    }

    // A Chests row's model when the type has none of its own (Gold Pile's four, Sunken Loot's two).
    static string? ModelOf(string type, Random random) =>
        QuestObjectTypes.Find(type) is { AnimatedModel: null } && ChestRows.Value.TryGetValue(type, out var row) && row.Models.Length > 0
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

    static byte[]? Blob(Thing thing, QuestObjectTypes.Info info)
    {
        var states = States.Value.GetValueOrDefault(thing.Type);
        if (states is not null)
            states = thing.Kind == Kind.Node
                ? states.Set("collected", thing.Done ? 1 : 0)
                // located 0 puts the object on the lantern pointer's list (globals.deepwater_poi); Soothing Coral is not
                // pointed at (3.29.0b).
                : states.Set("activated", thing.Done ? 1 : 0).Set("located", thing.Done || thing.Kind == Kind.SoothingCoral ? 1 : 0)
                    .Set("collected", thing.Done ? 1 : 0);
        return new QuestObject(thing.X, thing.Y, info.Components, thing.Rotation, null, thing.Model,
            TransitionState: (byte)(thing.Done ? info.States : 1), Opened: thing.Done) { StateMachine = states }.Blob();
    }

    static async Task Create(ClientState conn, Thing thing, uint id)
    {
        if (Info(thing.Type) is not { } info || Blob(thing, info) is not { } blob) return;
        var player = PlayerOf(conn);
        if (!await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(thing.Type), blob),
            $"0x02bb Allflame {thing.Kind} 0x{id:x} {thing.Type.Split('/')[^1]} at ({thing.X}, {thing.Y}){(thing.Done ? " (used)" : "")}")) return;
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id));
        if (info.Blocking) conn.SetBlocker(id, thing.X, thing.Y, Math.Max(1, info.Size));
    }

    // The Golden Lantern buff's instance on each connection.
    static readonly ConditionalWeakTable<ClientState, StrongBox<ushort>> GoldenBuffs = new();

    /// <summary>Creates the run's nodes and objects on this connection (as they are now), its Sulphur gauge (0x2c9 mode 1)
    /// and the character's Golden Lantern stacks.</summary>
    public static async Task Show(ClientState conn, DeepwaterRun run)
    {
        Thing[] things;
        int stacks;
        lock (run.Gate)
        {
            things = [.. run.Things];
            stacks = run.GoldenLanterns.GetValueOrDefault(CharacterName(conn));
        }
        foreach (var thing in things) await Create(conn, thing, thing.Id);
        await SendGauge(conn, run);
        GoldenBuffs.Remove(conn);
        if (stacks > 0) await SendGoldenBuff(conn, stacks);
    }

    static async Task SendGauge(ClientState conn, DeepwaterRun run)
    {
        int collected, cap;
        lock (run.Gate) (collected, cap) = (run.Sulphur, run.SulphurCap);
        // M: (collected, the run's possible total); the client fills its gauge a/b in five steps.
        if (DeepwaterRuleset.Sulphur((uint)collected, (uint)cap) is { } gauge)
            await PlayerOf(conn).Send(gauge, $"0x02c9 ruleset DeepwaterRules mode 1: Sulphur {collected}/{cap}");
    }

    // ---- Sulphur ----

    /// <summary>A lantern's light collects every uncollected node inside its full radius: the node's <c>collected</c> = 1,
    /// the lantern's flare, the run's Sulphur and the gauge, on every connection in the run. Called on each placement
    /// (and for the first lantern), never per tick.</summary>
    public static async Task Collect(IModuleContext module, ClientState conn, DeepwaterRun run, DeepwaterRun.Lantern lantern)
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
        foreach (var member in Members(conn))
        {
            await DeepwaterLanterns.Flare(member, lantern);
            foreach (var node in due) await SendStates(member, node);
            await SendGauge(member, run);
        }
        module.Log($"{CharacterName(conn)}: lantern {lantern.Order} at ({lantern.X}, {lantern.Y}) collected {due.Count} Sulphur nodes, +{due.Sum(t => t.Sulphur)} (run {total}/{run.SulphurCap})");
    }

    static async Task SendStates(ClientState conn, Thing thing)
    {
        if (Info(thing.Type) is not { } info || States.Value.GetValueOrDefault(thing.Type) is not { } states) return;
        int index = info.Components.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        states = thing.Kind == Kind.Node ? states.Set("collected", thing.Done ? 1 : 0)
            : states.Set("activated", thing.Done ? 1 : 0).Set("located", 1).Set("collected", thing.Done ? 1 : 0);
        await global::Game.SendWorldRecord(PlayerOf(conn), thing.Id, InstanceEntities.StateMachineUpdate(thing.Id, (byte)index, states),
            $"0x02bc Allflame {thing.Kind} 0x{thing.Id:x} {(thing.Kind == Kind.Node ? "collected" : "activated, located")}");
        if (thing.Kind != Kind.Node && QuestObject.TransitionableIndex(info.Components) is int t and >= 0 && thing.Done)
            await global::Game.SendWorldRecord(PlayerOf(conn), thing.Id, InstanceEntities.TransitionableUpdate(thing.Id, (byte)t, (byte)info.States),
                $"0x02bc Allflame {thing.Kind} 0x{thing.Id:x} Transitionable → {info.States}");
    }

    // ---- use ----

    /// <summary>C→S 0x24 on a chest or interactable of the run: refused (quietly) unless it is in a lantern's light, else
    /// the player walks up, it is used once (a chest is created again opened under a new id; an interactable's
    /// <c>activated</c> and <c>located</c> = 1 and its Transitionable's last state) and gives its reward.
    /// False when the object is not one of the run's.</summary>
    public static async Task<bool> Use(IModuleContext module, ClientState conn, DeepwaterRun run, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        Thing? thing;
        lock (run.Gate) thing = run.Things.FirstOrDefault(t => t.Id == use.TargetId && t.Kind != Kind.Node);
        if (thing is null) return false;
        var player = PlayerOf(conn);
        // A failed run's instance is dead: nothing in it opens any more.
        if (thing.Done || run.Failed || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea || Info(thing.Type) is not { } info)
        {
            await EndAction(conn);
            return true;
        }
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (!run.InLight((thing.X, thing.Y), now))
        {
            // ClientStrings 8447 DeepwaterErrorCannotInteractWhileDrowning; no message that shows it is known: refused quietly.
            module.Log($"{player.Character} <= 0x0024 Allflame {thing.Kind} 0x{thing.Id:x} at ({thing.X}, {thing.Y}) refused: not within Lanternlight");
            await EndAction(conn);
            return true;
        }
        int reach = Math.Max(1, info.Size);
        if (!TryBeginObjectWalk(conn, requestedAt, thing.X, thing.Y, reach, out var walk)) return true;
        var (action, start, here, _, arrive) = walk;
        if (!use.Started)
            await global::Game.SendWorldRecord(player, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, thing.X, thing.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: thing.Id));
        if (arrive > start) await Task.Delay(TimeSpan.FromSeconds(arrive - start));
        try
        {
            if (!PlayerActionCurrent(conn, action) || conn.PlayerLife == 0
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (thing.X, thing.Y), reach)) return true;
            uint was;
            lock (run.Gate)
            {
                if (thing.Done) return true;
                thing.Done = true;
                was = thing.Id;
                if (info.Components.Contains("Chest")) thing.Id = conn.TakeObjectId();
            }
            module.Log($"{player.Character} <= 0x0024 Allflame {thing.Kind} 0x{was:x} {thing.Type.Split('/')[^1]} at ({thing.X}, {thing.Y}) used");
            foreach (var member in Members(conn))
            {
                if (was != thing.Id)
                {
                    member.RemoveBlocker(was);
                    await global::Game.SendWorldRemoval(PlayerOf(member), was);
                    await Create(member, thing, thing.Id);
                }
                else await SendStates(member, thing);
            }
            await Reward(module, conn, run, thing);
        }
        finally { await EndAction(conn, action); }
        return true;
    }

    /// <summary>Ends the player's interaction (0x1ad), after a refusal or a use.</summary>
    public static Task EndAction(ClientState conn, int? action = null) =>
        global::Game.SendWorldRecord(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
            still: action is int a ? () => PlayerActionCurrent(conn, a) : null);

    static async Task Reward(IModuleContext module, ClientState conn, DeepwaterRun run, Thing thing)
    {
        var catalog = ItemsFor(conn);
        int level = Math.Clamp(run.Level, 1, 100);
        switch (thing.Kind)
        {
            case Kind.SunkenLoot:
            {
                // G: 3-6 currency items by the server's currency weights (ItemDrops.Currency) at the run's level.
                var pool = ItemDrops.Currency.Select(c => (Type: catalog.Find(c.Id), c.Weight))
                    .Where(c => c.Type is { Supported: true } && c.Type.DropLevel <= level).ToList();
                var drops = new List<ItemDrops.Drop>();
                for (int i = Random.Shared.Next(3, 7); i > 0 && pool.Count > 0; i--)
                {
                    double roll = Random.Shared.NextDouble() * pool.Sum(p => p.Weight);
                    var pick = pool[^1];
                    foreach (var p in pool) if ((roll -= p.Weight) < 0) { pick = p; break; }
                    drops.Add(new(ItemCatalog.Create(pick.Type!) with { StackSize = 1, ItemLevel = (byte)level }, pick.Type!));
                }
                await Drop(conn, thing, ItemDrops.Stacked(drops));
                break;
            }
            case Kind.GoldPile or Kind.Clam:
            {
                // G: level × 8-15 gold; a Treasure Clam half (its rare uniques are not built).
                int amount = level * Random.Shared.Next(8, 16) / (thing.Kind == Kind.Clam ? 2 : 1);
                if (catalog.Find(ItemDrops.GoldId) is { Supported: true } gold && amount > 0)
                    await Drop(conn, thing, [new(ItemCatalog.Create(gold) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) }, gold)]);
                break;
            }
            case Kind.Bottle:
                // The bottle item as plain loot after the chest's item_drop_delay; opening it is a later step.
                // The use's action ends at once; the drop follows on its own.
                if (catalog.Find(BottleItem) is { Supported: true } bottle)
                {
                    var instance = conn.Instance;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(BottleDelay));
                            if (!conn.Closed && !conn.LeavingArea && conn.Instance == instance)
                                await Drop(conn, thing, [new(ItemCatalog.Create(bottle) with { ItemLevel = (byte)level }, bottle)]);
                        }
                        catch (Exception ex) { module.Log($"{CharacterName(conn)}: the bottle's drop failed: {ex.Message}"); }
                    });
                }
                else module.Log($"{CharacterName(conn)}: {BottleItem} is not an item the server writes; the bottle drops nothing");
                break;
            case Kind.GoldenLantern:
            {
                int stacks;
                lock (run.Gate) stacks = run.GoldenLanterns[CharacterName(conn)] = run.GoldenLanterns.GetValueOrDefault(CharacterName(conn)) + 1;
                await SendGoldenBuff(conn, stacks);
                break;
            }
            case Kind.SoothingCoral:
                await Refill(conn);
                break;
            case Kind.Kindling:
                lock (run.Gate) run.Total += 3;
                await DeepwaterLanterns.SendCount(conn, run);
                break;
        }
    }

    static async Task Drop(ClientState conn, Thing thing, IReadOnlyList<ItemDrops.Drop> drops)
    {
        if (!GameEvents.AllowsLoot(conn) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var (remote, stream, ciphers) = conn.Wire;
        int ring = Math.Max(2, (Info(thing.Type)?.Size ?? 1) / 2 + 2);
        for (int i = 0; i < drops.Count; i++)
        {
            double angle = i * 2.399963229728653 + Random.Shared.NextDouble();
            var at = conn.Layout.Near((thing.X + (int)Math.Round(Math.Cos(angle) * ring), thing.Y + (int)Math.Round(Math.Sin(angle) * ring)));
            var ground = new WorldItemObject(at.X, at.Y, drops[i].Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, id, ground, $"Allflame {thing.Kind} 0x{thing.Id:x} drops {drops[i].Type.Name} ×{drops[i].Item.StackSize}", justDropped: true);
        }
    }

    /// <summary>Golden Lantern's Glow (BuffDefinitions 2798) with its stacks, one per Golden Lantern used, for the rest of
    /// the run: the same instance again with the new count.</summary>
    static async Task SendGoldenBuff(ClientState conn, int stacks)
    {
        var box = GoldenBuffs.GetValue(conn, c => new StrongBox<ushort>(c.NextBuff++));
        await global::Game.SendWorldRecord(PlayerOf(conn), PlayerId,
            InstanceEntities.AddBuff(PlayerId, box.Value, DeepwaterConstants.GoldenLanternBuff, stacks: (ushort)Math.Clamp(stacks, 1, ushort.MaxValue)),
            $"0x01c1 Golden Lantern's Glow × {stacks}");
    }

    // Soothing Coral: life, mana and energy shield full, and the flasks' charges.
    static async Task Refill(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) return;
            conn.PlayerLife = Math.Max(conn.PlayerLife, LifeCap(conn));
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, refill: true);
            conn.PlayerMana = Math.Max(conn.PlayerMana, (uint)UnreservedMana(conn));
        }
        var (remote, stream, ciphers) = conn.Wire;
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, PlayerLifeUpdate(conn), $"0x01b1 Soothing Coral: life {conn.PlayerLife}");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 Soothing Coral: mana {conn.PlayerMana}");
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.LifeUpdate(PlayerId, conn.EnergyShield.Current, pool: 2),
            $"0x01b1 Soothing Coral: energy shield {conn.EnergyShield.Current}");
        await GainFlaskCharges(remote, stream, ciphers, conn, int.MaxValue, "Soothing Coral");
    }

    // ---- drops ----

    /// <summary>GameEvents.Drops in a run: the league's monsters drop map-related items at −50 %
    /// (<c>map_related_item_drop_chance_+%_final_from_league</c> −50, DeepwaterBase.ot), and the killer's Golden Lantern
    /// stacks add +10 % quantity and +30 % rarity each (Ruthless +3 % / +10 %; the wiki), applied as multipliers (G).</summary>
    public static void Drops(DropRoll roll, DeepwaterRun run)
    {
        if (roll.Monster.VarietyId.StartsWith(LeagueMonsters, StringComparison.Ordinal)) roll.MapRelated *= 0.5;
        int stacks;
        lock (run.Gate) stacks = run.GoldenLanterns.GetValueOrDefault(CharacterName(roll.Conn));
        if (stacks == 0) return;
        var (rarity, quantity) = LeagueFor(roll.Conn).HardMode ? (10, 3) : (30, 10);
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
