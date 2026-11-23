using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

/// <summary>
/// The town objects of Kingsmarch (<c>KalguuranSettlersLeague</c>): the buildings, platforms, wharves and props the server
/// must create — the client draws the room's own doodads, but the town's floor, docks and buildings are objects of the
/// <c>Metadata/Terrain/Leagues/Settlers/HubTown/Objects/*</c> types, which is why the authored furniture floats without them.
///
/// The list is <c>data/kingsmarch-town.json</c> (from the client's <c>room_doodads</c> rows of the <c>kingsmarch.arm</c>
/// room, index 1984): type, model, room-local cell and angle. A type whose components this blob cannot write is skipped and
/// logged, never sent half-written: a short blob makes the client report "Buffer Underflow" and quit.
///
/// <c>data/kingsmarch-limit.txt</c> is how many objects to create (0 = none). It is meant to be raised a step at a time —
/// 1, then 10, then all — because the first all-at-once attempt (2026-11-17 00:2x) made the client quit as the town loaded,
/// so at least one component's layout here is still wrong. The safest types (Positioned, BaseEvents, Animated,
/// StateMachine, InteractionAction, MinimapIcon) come first, so the lowest limits exercise the least uncertain blobs.
/// </summary>
internal static class KingsmarchTown
{
    const string Area = "KalguuranSettlersLeague";
    const string DataFile = "data/kingsmarch-town.json";
    const string LimitFile = "data/kingsmarch-limit.txt";
    // Dev: only the object types whose path contains one of these words (comma separated; empty = all).
    const string FilterFile = "data/kingsmarch-types.txt";
    const string AnimatedFile = "data/kingsmarch-animated.txt";
    // Types this client does not register at runtime (found by the acceptance sweep): never sent.
    const string BlockedFile = "data/kingsmarch-blocked.txt";
    // Dev: send every row as this object type instead of the one the room table names, keeping the row's own model. The
    // HubTown types are not in the client's object register, so the town's structures have to be carried by a type the
    // client knows; blocking is what decides whether the result can be walked through.
    const string TypeFile = "data/kingsmarch-type.txt";
    // Models the client does not know (found the same way): a row whose model contains one of these is skipped.
    const string BlockedModelsFile = "data/kingsmarch-blocked-models.txt";
    // Types the client refuses (found by the run loop): sent as the base type instead, model kept.
    const string FallbackFile = "data/kingsmarch-fallback.txt";
    // Per-type object state, e.g. "FarmingPlot plant=1" (the crop; the plot swaps its own model per plant value) or
    // "FarmingPlotInteractable visible=1". A value set here is sent with the StateMachine "server value wins" flag (0x02),
    // without which the client reverts the state to the type's default.
    const string StatesFile = "data/kingsmarch-states.txt";
    // The farm's crops and harvest, per character, so they survive a reload, a new area load and a server restart.
    const string FarmFile = "data/kingsmarch-farm.json";
    // The town's clickable boards (treasury/recruiter/farm) only spawn when this file exists; see Boards() for why they are
    // off by default.
    const string BoardsFile = "data/kingsmarch-boards.txt";

    // The town's own panels are gated on the IsMapOwner quest flag (NPCTalk 667 "Manage Town", 670 "Town Recruitment",
    // 672 "Disenchanting", 674 "Shipping"): without it their options are in the list but the server refuses them. The town
    // instance belongs to the player, so they own it.
    static void EnsureOwnerFlag(IPlayer player)
    {
        var conn = Program.ConnOf(player);
        if (conn is null || conn.HasQuestFlag("IsMapOwner")) return;
        lock (conn.Monsters) conn.QuestFlags.Add("IsMapOwner");
    }

    // Brackets, Functions, Render, Preload and Targetable write nothing: their client readers are the ret-family (0x1400f1180)
    // or they are client-block only, so a type that lists them is still writable.
    static readonly HashSet<string> Supported =
        ["Positioned", "BaseEvents", "Animated", "StateMachine", "TriggerableBlockage", "InteractionAction", "MinimapIcon",
         "Functions", "Brackets", "Render", "Preload", "Targetable"];

    // Components whose wire layout this file is sure of (InstanceEntities/QuestObject/HideoutObject).
    static readonly HashSet<string> Sure =
        ["Positioned", "BaseEvents", "Animated", "StateMachine", "InteractionAction", "MinimapIcon"];

    sealed record TownObject(string Type, string Model, int X, int Y, float Angle);

    static List<TownObject>? _objects;
    static (int X, int Y) _origin = (138, 138);
    static Dictionary<string, (string[] Components, string[] States)>? _types;
    // Per connection: the next object row and id. A connection is one area load, so leaving and coming back starts over.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Progress> _progress = [];
    static readonly object _lock = new();
    // Spawning is opt-in per module load (see Tick): /village_spawn turns it on for this session.
    static bool _armed;

    /// <summary>Lets the next ticks spawn the town's objects (dev: /village_spawn).</summary>
    public static void Arm() => _armed = true;

    /// <summary>An area load: the client holds none of the town's objects yet, so it is built again from the saved farm.</summary>
    public static Task Entered(IPlayer player)
    {
        lock (_lock)
        {
            var progress = _progress.GetOrCreateValue(player);
            progress.Next = 0;
            progress.Plots.Clear();
            Console.WriteLine($"[gateway] kingsmarch area load: {player.AreaId} character {player.Character}");
            // The client is still loading the area; objects sent into a loading area are dropped.
            progress.Ready = Environment.TickCount64 / 1000.0 + 3.0;
            Restore(progress, player.Character);
            // A real area load is the one case where sending the town again is safe, so the town needs no dev command.
            // The player's area is not updated yet while the area is being generated, so this cannot test for the town.
            _armed = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>The connection's town state, restored from the saved farm the first time anything asks for it. The store has
    /// to be readable in a load where the town's objects were not placed too: the port asks what the town is holding before
    /// any plot exists in the client.</summary>
    static Progress State(IPlayer player)
    {
        Progress progress;
        lock (_lock)
        {
            progress = _progress.GetOrCreateValue(player);
            if (progress.Who.Length == 0) Restore(progress, player.Character);
        }
        return progress;
    }

    sealed class Progress
    {
        public int Next;
        // Ids start at a random-ish base so that a module reload (which starts a new Progress) does not re-send the same
        // ids into a town the client already has: re-creating an id it still knows leaves the old object in place.
        public uint Id = 0x5A000000u + (uint)(Environment.TickCount64 & 0xFF) * 0x100;
        // The farm plots this connection created: a click on one of these ids plants or harvests.
        public readonly Dictionary<uint, Plot> Plots = [];
        // The character whose farm this is, the crops it had stored when the area loaded, and what it has harvested.
        public string Who = "";
        public List<SavedPlot> Saved = [];
        public readonly Dictionary<string, int> Store = [];
        // Before this (seconds, Environment.TickCount64 based) the area is still loading and nothing may be sent.
        public double Ready;
        // Plots removed for a replant, waiting for their re-create (the client's destroy is deferred).
        public readonly List<(double At, Plot Plot, int X, int Y)> Pending = [];
        // Which crop the next planting takes (the plots take turns at the five crops).
        public int Rotation;
        // The workers hired for this character's town; each tends one plot, works the mine or the smeltery, or sits idle.
        public readonly List<Worker> Workers = [];
        // When the mine's and the smeltery's running cycle started (seconds, Environment.TickCount64 based), so a worker
        // set to either job brings its produce in MineSeconds/SmeltSeconds later. The mine digs one ore a worker a cycle;
        // the smeltery refines one ore a worker a cycle.
        public double MineStartedAt;
        public double SmeltStartedAt;
        // The mine's ore rotation, like the plots' Rotation.
        public int MineRotation;
        // The town's clickable boards, since the client never builds its Kingsmarch panel in this build.
        public readonly Dictionary<uint, TownBoard> Boards = [];
    }

    /// <summary>What a click on one of the town's boards does.</summary>
    enum BoardAction
    {
        Treasury,   // put gold from the character's stash into the town's treasury
        Recruit,    // hire the recruiter's first candidate
        Farm,       // report what the plots hold
    }

    /// <summary>A board standing in the town: the client's own Kingsmarch panel is never built in this build, so the
    /// town's buttons are objects — walk up to the board, click it and the action happens.</summary>
    sealed class TownBoard
    {
        public BoardAction Action;
    }

    /// <summary>A hired worker: the plot it tends (-1 = idle) and what it has brought in.</summary>
    sealed class Worker    {
        public int Order;
        public int Plot = -1;
        public int Harvests;
        public string Name = "";
    }

    /// <summary>One of the town's six farm plots: what grows there and since when.</summary>
    sealed class Plot
    {
        public string Who = "";    // the character whose farm this plot belongs to
        public uint ObjectId;      // the id the client knows this plot by (it changes on every replant)
        public int Order;          // 0..5, the plot's index in the room
        public byte StateIndex;    // the StateMachine component's index, for its 0x2bc update
        public string[] States = [];   // the type's shared state names, in wire order
        public TownObject Row = null!; // the room row, so the plot can be re-created in another plant state
        public (string[] Components, string[] States) Type;
        public int Crop;           // 0 = bare, 1..5 = the plant state value
        public double PlantedAt;
        // When the client was last sent this plot. A plot is never removed again within a few seconds of being created: the
        // client's destroy is deferred and tearing down an object it has only just built is what corrupted its object map.
        public double CreatedAt;
        public int Harvests;
    }

    /// <summary>A plot as stored in <c>data/kingsmarch-farm.json</c>.</summary>
    sealed class SavedPlot
    {
        public int Crop { get; set; }
        public double Planted { get; set; }
        public int Harvests { get; set; }
    }

    /// <summary>One character's farm: its plots, its workers and what it has harvested. MineStarted and SmeltStarted hold
    /// the running job cycles so a reload does not hand out free ore (0 = none running, restored to now).</summary>
    sealed class SavedFarm
    {
        public List<SavedPlot> Plots { get; set; } = [];
        public List<SavedWorker> Workers { get; set; } = [];
        public Dictionary<string, int> Store { get; set; } = [];
        public double MineStarted { get; set; }
        public double SmeltStarted { get; set; }
    }

    /// <summary>A worker as stored in <c>data/kingsmarch-farm.json</c>.</summary>
    sealed class SavedWorker
    {
        public int Plot { get; set; } = -1;
        public int Harvests { get; set; }
        public string Name { get; set; } = "";
    }

    static readonly Dictionary<string, SavedFarm> _farms = [];

    /// <summary>The character's saved farm, loaded from disk once per module load.</summary>
    static SavedFarm Farm(string who)
    {
        lock (_farms)
        {
            if (_farms.TryGetValue(who, out var farm)) return farm;
            if (File.Exists(FarmFile))
            {
                try
                {
                    var all = JsonSerializer.Deserialize<Dictionary<string, SavedFarm>>(File.ReadAllText(FarmFile));
                    if (all is not null)
                        foreach (var (key, value) in all) _farms[key] = value;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[gateway] kingsmarch: {FarmFile} is unreadable, starting the farm over: {e.Message}");
                }
            }
            if (!_farms.TryGetValue(who, out farm)) _farms[who] = farm = new SavedFarm();
            return farm;
        }
    }

    static void SaveFarm()
    {
        lock (_farms)
        {
            try
            {
                File.WriteAllText(FarmFile, JsonSerializer.Serialize(_farms, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[gateway] kingsmarch: cannot write {FarmFile}: {e.Message}");
            }
        }
    }

    // The client registers object types only from its own dat tables at startup (Metadata/GameObjectRegister: MonsterVarieties,
    // Chests, NPCs, Projectiles, Characters, Pet, MiscObjects, HideoutDoodads) and nothing a server sends can add one. Six
    // HubTown types are not in it, so each stands as the accepted type with the same components and keeps its own model
    // (the model is what the client draws; only the type key must be one it knows).
    static readonly Dictionary<string, string> Substitute = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeAnimated"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeLarge"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPCIdle"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeNPC"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Seagull"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/SeagullIdle",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Chicken"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep",
        ["Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Peacock"] = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/Sheep",
    };

    sealed class Counter { public int Value; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Counter> _diag = [];

    /// <summary>Creates the town's objects up to <c>data/kingsmarch-limit.txt</c>, a few per tick, on the live connection.</summary>
    public static async Task Tick(IPlayer player, double now)
    {
        // Diagnosis: the first two ticks of a connection say what the module sees.
        if (_diag.GetValue(player, _ => new Counter()).Value++ < 2)
            Console.WriteLine($"[gateway] kingsmarch tick: area '{player.AreaId}' character {player.Character} seed {player.Seed} limit {Limit()} objects {Ordered().Count} cwd {Directory.GetCurrentDirectory()} file {File.Exists(DataFile)} filter {Filter().Length}");
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return;
        EnsureOwnerFlag(player);
        // Spawning waits for an explicit /village_spawn: a module reload starts with an empty Progress, and re-sending the
        // town's objects into an area where the client already has them is what killed the client on 2026-11-19 (the
        // client logged ObjectInWakeZone … already in the wake zone and dropped the connection).
        if (!_armed) return;
        _types ??= Types();

        Progress progress;
        lock (_lock)
        {
            progress = _progress.GetOrCreateValue(player);
            if (progress.Who.Length == 0) Restore(progress, player.Character);
        }
        // The tick's own time value is not the clock the rest of this file uses, so wait on Environment.TickCount64 here.
        double clock = Environment.TickCount64 / 1000.0;
        if (clock < progress.Ready) return;
        // The boards are the town's buttons and stand on their own: they are created whatever the object limit says, so a
        // limit of 0 (no town furniture) still leaves a way to fund the treasury, hire a worker and read the farm.
        await Boards(player, progress, clock);
        int limit = Limit();
        if (limit <= 0) return;

        var objects = Ordered();
        await Flush(player, progress, clock);
        await Work(player, progress, clock);
        await TownJobs(player, progress, clock);

        int created = 0;
        while (progress.Next < objects.Count && progress.Next < limit && created < 20)
        {
            var row = objects[progress.Next++];
            var o = Substitute.TryGetValue(row.Type, out var stand) ? row with { Type = stand } : row;
            var fallback = Fallback();
            if (fallback.Length > 0 && fallback.Any(f => o.Type.EndsWith(f, StringComparison.OrdinalIgnoreCase)))
                o = o with { Type = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/DecoUpgradeBase" };
            string forced = TypeOverride();
            if (forced.Length > 0) o = o with { Type = forced };
            if (!_types.TryGetValue(o.Type, out var type))
            {
                Console.WriteLine($"[gateway] kingsmarch: {o.Type} is not a HubTown object type, not created");
                continue;
            }
            if (!type.Components.All(Supported.Contains))
            {
                Console.WriteLine($"[gateway] kingsmarch: {o.Type} ({string.Join(", ", type.Components)}) cannot be written yet, not created");
                continue;
            }
            // A plot is created in the state of what grows on it, since that state is what the client builds its model from.
            int stateIndex = Array.IndexOf(type.Components, "StateMachine");
            bool plot = stateIndex >= 0 && o.Type.EndsWith("/FarmingPlot", StringComparison.OrdinalIgnoreCase);
            SavedPlot? saved = plot && progress.Plots.Count < progress.Saved.Count ? progress.Saved[progress.Plots.Count] : null;
            // A plot the character's workers were left on comes back with them on it.
            int order = progress.Plots.Count;
            int hands = plot ? progress.Workers.Count(w => w.Plot == order) : 0;
            uint id = progress.Id++;
            int x = _origin.X + o.X, y = _origin.Y + o.Y;
            await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type),
                    Blob(x, y, o, type, plot ? saved?.Crop ?? 0 : null, plot ? hands : null)),
                $"0x02bb create Kingsmarch object 0x{id:x} {o.Type} at ({x}, {y}) plot {plot} workers {hands}");
            // No 0x1a9: the create blob's object flag (bit 1) already puts the object in the wake zone, and the explicit
            // enter is what the client answers with "ObjectInWakeZone was called … already in the wake zone".
            if (plot)
                lock (_lock) progress.Plots[id] = new Plot
                {
                    Who = progress.Who, ObjectId = id, Order = progress.Plots.Count, StateIndex = (byte)stateIndex, States = type.States,
                    Row = o, Type = type, Crop = saved?.Crop ?? 0, PlantedAt = saved?.Planted ?? 0, Harvests = saved?.Harvests ?? 0,
                    CreatedAt = clock,
                };
            created++;
        }
    }

    /// <summary>The town's clickable boards, on the plaza next to where the player arrives (590, 655): the treasury, the
    /// recruiter and the farm report. They are <c>NoticeboardInteractable</c> — the same components as the farm plots, which
    /// the client has been building and clicking without trouble.</summary>
    static readonly (BoardAction Action, int X, int Y, string Model)[] BoardRows =
    [
        (BoardAction.Treasury, 578, 668, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_01.ao"),
        (BoardAction.Recruit, 602, 668, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/recruitment_noticeboard_02.ao"),
        (BoardAction.Farm, 590, 680, "Metadata/Terrain/Doodads/Leagues/Settlers/Kingsmarch/farming_Interactable01_01.ao"),
    ];

    /// <summary>How much gold one click on the treasury board puts in (the character's stash pays, as with the NPC).</summary>
    const int TreasuryPick = 1000;

    /// <summary>Creates the town's boards — once per area load, and independent of the object limit, since they are the only
    /// way to fund the treasury, hire a worker or read the farm without a chat command. Off unless
    /// <c>data/kingsmarch-boards.txt</c> exists: the client answers a server-made <c>NoticeboardInteractable</c> with
    /// <c>Error executing GEAL … expected a number type</c> and then dies in its object map (fault RVA 0x148b38, 2026-11-19),
    /// so a board needs a type the client builds without its doodad-line data.</summary>
    static async Task Boards(IPlayer player, Progress progress, double clock)
    {
        if (!File.Exists(BoardsFile)) return;
        lock (_lock) if (progress.Boards.Count > 0) return;
        _types ??= Types();
        const string type = "Metadata/Terrain/Leagues/Settlers/HubTown/Objects/NoticeboardInteractable";
        if (!_types.TryGetValue(type, out var shape) || !shape.Components.All(Supported.Contains)) return;
        foreach (var (action, x0, y0, model) in BoardRows)
        {
            var row = new TownObject(type, model, x0, y0, 0f);
            uint id = progress.Id++;
            int x = _origin.X + row.X, y = _origin.Y + row.Y;
            await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), Blob(x, y, row, shape)),
                $"0x02bb create Kingsmarch board 0x{id:x} {action} at ({x}, {y})");
            lock (_lock) progress.Boards[id] = new TownBoard { Action = action };
        }
    }

    /// <summary>A click on one of the town's boards.</summary>
    static async Task<bool> Pushed(IPlayer player, BoardAction action)
    {
        switch (action)
        {
            case BoardAction.Treasury:
                await player.Tell("Kingsmarch: " + await FundTreasury(player, TreasuryPick));
                return true;
            case BoardAction.Recruit:
                await player.Tell("Kingsmarch: " + await Recruit(player, 1));
                return true;
            default:
                await player.Tell(StoreText(player));
                return true;
        }
    }

    /// <summary>Dev: a stash on the player's own cell. A <c>0x2bb</c> object never stamps the area's walk grid, so this is the
    /// reference for "does the client block a server-made object in this area at all" — in Lioneye's Watch the same object
    /// blocks walkers. Its type is <c>blocking = true</c> in the .ot and its hull comes from the model's AO walk shapes.</summary>
    public static async Task<string> Box(IPlayer player)
    {
        var progress = State(player);
        const string type = "Metadata/MiscellaneousObjects/Stash";
        _types ??= Types();
        if (!_types.TryGetValue(type, out var shape))
            return $"no type {type} in data/object-types.json";
        if (!shape.Components.All(Supported.Contains))
            return $"{type} needs components no writer has: {string.Join(", ", shape.Components)}";
        var (px, py) = player.Position;
        // The type owns its model: writing a made-up path makes the client throw "… .ao is non-virtual" (2026-11-20).
        var row = new TownObject(type, "", px, py, 0f);
        uint id = progress.Id++;
        await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), Blob(px, py, row, shape)),
            $"0x02bb create test stash 0x{id:x} at ({px}, {py})");
        return $"a stash object 0x{id:x} on your cell ({px}, {py})";
    }

    /// <summary>Dev: one object of any type the client has registered, carrying an arbitrary model, next to the player. The town's
    /// own <c>HubTown/Objects/*</c> types are not in the client's register (so a 0x2bb with one is refused); the types in the
    /// dat tables' eight object registers — chests, miscellaneous objects, NPCs — are, and their <c>Positioned</c> carries
    /// <c>blocking</c> plus a walk hull from the model's AO, which is what makes a server-made object stop a walker.</summary>
    public static async Task<string> Prop(IPlayer player, string type, string model, int dx, int dy)
    {
        var progress = State(player);
        _types ??= Types();
        if (!_types.TryGetValue(type, out var shape))
            return $"no type {type} in data/object-types.json";
        if (!shape.Components.All(Supported.Contains))
            return $"{type} needs components no writer has: {string.Join(", ", shape.Components)}";
        var (px, py) = player.Position;
        int x = px + dx, y = py + dy;
        var row = new TownObject(type, model, x, y, 0f);
        uint id = progress.Id++;
        await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type),
                Blob(x, y, row, shape, modelFromWire: true)),
            $"0x02bb create prop 0x{id:x} {type} model '{model}' at ({x}, {y})");
        return $"prop 0x{id:x} of {type} at ({x}, {y}) with model {model}";
    }

    /// <summary>Dev: server-side blockers over a rectangle, to see whether the client's walker honours one at all.</summary>
    public static string Block(IPlayer player, int x0, int y0, int x1, int y1)
    {
        var conn = Program.ConnOf(player);
        int n = 0;
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            {
                conn.SetBlocker(0x5B000000u + (uint)n, x, y, 4);
                n++;
            }
        return $"blocked {n} cell(s) from ({x0}, {y0}) to ({x1}, {y1})";
    }

    /// <summary>Dev: a small ASCII picture of the walk grid around the player, one line per row, '#' blocked, '.' open,
    /// 'P' the player. Answers "is the stamp where I think it is" without walking anywhere.</summary>
    public static string Cells(IPlayer player, int halfX, int halfY)
    {
        var conn = Program.ConnOf(player);
        if (conn is null || conn.Layout.Walkable is not { } walk) return "no walk grid in this area";
        var (px, py) = player.Position;
        halfX = Math.Clamp(halfX, 1, 60);
        halfY = Math.Clamp(halfY, 1, 30);
        var text = new System.Text.StringBuilder();
        for (int y = py + halfY; y >= py - halfY; y--)
        {
            for (int x = px - halfX; x <= px + halfX; x++)
                text.Append(x == px && y == py ? 'P' : walk(x, y) ? '.' : '#');
            text.Append(' ').Append(y).Append('\n');
        }
        return $"walk grid {px - halfX}..{px + halfX} x {py - halfY}..{py + halfY} (row ends carry y):\n{text}";
    }

    /// <summary>The furthest cell the walk grid allows along the straight line from (x0, y0) to (x1, y1), stepping one
    /// cell at a time. The town's walls are straight runs of cells, so clipping the line is enough to stop a click that
    /// lands on the far side of one: the player walks up to the wall instead of through it.</summary>
    internal static (int X, int Y) Clip(Func<int, int, bool> walk, int x0, int y0, int x1, int y1)
    {
        int dx = x1 - x0, dy = y1 - y0;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps == 0) return (x1, y1);
        int lastX = x0, lastY = y0;
        for (int i = 1; i <= steps; i++)
        {
            int x = x0 + (int)Math.Round(dx * (double)i / steps);
            int y = y0 + (int)Math.Round(dy * (double)i / steps);
            if (x == lastX && y == lastY) continue;
            if (!walk(x, y)) return (lastX, lastY);
            lastX = x;
            lastY = y;
        }
        return (x1, y1);
    }

    /// <summary>Where a click should send the player when the town's walls are in the way. Live 2026-11-21 clipping the
    /// straight line alone trapped the player: standing beside a wall or a barrel, the first cell of any click towards the
    /// open ground could be blocked, so the move was refused again and again and the character twitched in place while
    /// refusing to leave. So the straight line is only the first candidate — the line is also tried bent to either side,
    /// and the bent line that travels furthest wins, which walks the player along the obstacle instead of pinning them.
    /// Returns the player's own cell only when no direction moves at all; the caller lets such a click through untouched
    /// rather than answering it, so a corner can never trap anyone.</summary>
    internal static (int X, int Y) Slide(Func<int, int, bool> walk, int x0, int y0, int x1, int y1)
    {
        var best = Clip(walk, x0, y0, x1, y1);
        double bestLength = Distance(x0, y0, best);
        double full = Distance(x0, y0, (x1, y1));
        if (bestLength >= full) return best;
        double dx = x1 - x0, dy = y1 - y0;
        foreach (double degrees in new[] { 30.0, -30.0, 55.0, -55.0, 80.0, -80.0 })
        {
            double radians = degrees * Math.PI / 180.0;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            int bx = x0 + (int)Math.Round(dx * cos - dy * sin);
            int by = y0 + (int)Math.Round(dx * sin + dy * cos);
            var bent = Clip(walk, x0, y0, bx, by);
            double length = Distance(x0, y0, bent);
            if (length > bestLength)
            {
                best = bent;
                bestLength = length;
            }
            if (bestLength >= full * 0.75) break;
        }
        return best;
    }

    static double Distance(int x0, int y0, (int X, int Y) cell)
        => Math.Sqrt((double)(cell.X - x0) * (cell.X - x0) + (double)(cell.Y - y0) * (cell.Y - y0));

    /// <summary>Dev switch: <c>data/kingsmarch-bounce.txt</c> present turns the wall handling on (delete it to stop).
    /// The client's own walker ignores the server's walk grid — live 2026-11-20 a click walked straight through a wall
    /// the server had stamped as blocked — so the module clips such a move to the wall face instead.</summary>
    internal static bool BounceOn() => File.Exists("data/kingsmarch-bounce.txt");

    /// <summary>Whether the area is the town, so the bounce only ever applies here.</summary>
    internal static bool IsTown(string areaId) => string.Equals(areaId, Area, StringComparison.Ordinal);

    /// <summary>Loads the character's saved farm into this area load: its crop on each plot and what it has harvested,
    /// the workers (a Plot of MineJob/SmelterJob works the mine or the smeltery) and the running job cycles.</summary>
    static void Restore(Progress progress, string? character)
    {
        progress.Who = character ?? "";
        var farm = Farm(progress.Who);
        progress.Saved = farm.Plots;
        foreach (var (crop, count) in farm.Store) progress.Store[crop] = count;
        double now = Environment.TickCount64 / 1000.0;
        progress.MineStartedAt = farm.MineStarted > 0 ? farm.MineStarted : now;
        progress.SmeltStartedAt = farm.SmeltStarted > 0 ? farm.SmeltStarted : now;
        progress.Workers.Clear();
        foreach (var saved in farm.Workers)
            progress.Workers.Add(new Worker { Order = progress.Workers.Count, Plot = saved.Plot, Harvests = saved.Harvests, Name = saved.Name });
    }

    /// <summary>The objects with the surely-written types first, so a low limit only exercises those.</summary>
    static List<TownObject> Ordered()
    {
        var all = Objects();
        var sure = new List<TownObject>(all.Count);
        var rest = new List<TownObject>();
        foreach (var o in all)
        {
            if (_types is not null && _types.TryGetValue(o.Type, out var t) && t.Components.All(Sure.Contains)) sure.Add(o);
            else rest.Add(o);
        }
        sure.AddRange(rest);
        var badModels = BlockedModels();
        if (badModels.Length > 0) sure = [.. sure.Where(o => !badModels.Any(b => o.Model.Contains(b, StringComparison.OrdinalIgnoreCase)))];
        var blocked = Blocked();
        if (blocked.Length > 0) sure = [.. sure.Where(o => !blocked.Any(b => o.Type.EndsWith(b, StringComparison.OrdinalIgnoreCase)))];
        var filter = Filter();
        // A word with a slash in it is a whole type path (exact); a bare word matches any type path that contains it.
        if (filter.Length > 0)
            sure = [.. sure.Where(o => filter.Any(f => f.Contains('/')
                ? string.Equals(o.Type, f, StringComparison.OrdinalIgnoreCase)
                : o.Type.Contains(f, StringComparison.OrdinalIgnoreCase)))];
        return sure;
    }

    static string[] Fallback()
    {
        try
        {
            if (!File.Exists(FallbackFile)) return [];
            return File.ReadAllText(FallbackFile).Replace(',', ' ').Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return []; }
    }

    static string[] BlockedModels()
    {
        try
        {
            if (!File.Exists(BlockedModelsFile)) return [];
            return File.ReadAllText(BlockedModelsFile).Replace(',', ' ').Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return []; }
    }

    static string[] Blocked()
    {
        try
        {
            if (!File.Exists(BlockedFile)) return [];
            return File.ReadAllText(BlockedFile).Replace(',', ' ').Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return []; }
    }

    static string[] Filter()
    {        try
        {
            if (!File.Exists(FilterFile)) return [];
            var text = File.ReadAllText(FilterFile).Replace(',', ' ');
            var words = text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
            return words;
        }
        catch (IOException) { return []; }
    }

    /// <summary>The type every row is sent as, from <c>data/kingsmarch-type.txt</c>; empty means "as the room table says".</summary>
    static string TypeOverride()
    {
        try
        {
            return File.Exists(TypeFile) ? File.ReadAllText(TypeFile).Trim() : "";
        }
        catch (IOException) { return ""; }
    }

    /// <summary>Dev: data/kingsmarch-animated.txt = "type" makes every Animated component 0 (the type own model).</summary>
    static bool UseTypeModel()
    {
        try
        {
            return File.Exists(AnimatedFile) && File.ReadAllText(AnimatedFile).Trim().Equals("type", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    static int Limit()
    {
        try
        {
            return File.Exists(LimitFile) && int.TryParse(File.ReadAllText(LimitFile).Trim(), out int n) ? Math.Clamp(n, 0, 10000) : 0;
        }
        catch (IOException) { return 0; }
    }

    static byte[] Blob(int x, int y, TownObject o, (string[] Components, string[] States) type, int? plant = null, int? workers = null,
        bool? modelFromWire = null)
    {
        var over = Overrides(o.Type);
        bool typeModel = modelFromWire is { } send ? !send : over.TryGetValue("animated", out int animated) ? animated == 0 : UseTypeModel();
        var w = new BlobWriter();
        w.U8(0);      // object flags
        w.U8(0);      // base data entry count
        foreach (string c in type.Components)
        {
            switch (c)
            {
                case "Positioned":
                    w.I32(x).I32(y).F32(o.Angle).U16(0).U8(0).U8(0x02).U8(0).U8(1);
                    break;
                case "Animated":
                    // Writing the model path is what a variant doodad needs, but some town types own their model (their
                    // animated_object is the one the object already is): those get 0, "use the type's own". Dev switch:
                    // data/kingsmarch-animated.txt = "type" forces 0 for every type.
                    if (typeModel) w.U8(0); else w.U8(1).WStr32(o.Model);
                    break;
                case "StateMachine":
                    w.U8((byte)type.States.Length).U8(0);
                    foreach (string s in type.States)
                    {
                        // A state sent with flags 0 is reverted by the client to the type's default; flags 0x02 keep what the
                        // server says. Only the states this file names get that flag, so nothing else changes behaviour.
                        bool said = over.TryGetValue(s, out int value);
                        if (plant is { } crop && s == "plant") { value = crop; said = true; }
                        if (workers is { } hands && s == "num_workers") { value = hands; said = true; }
                        if (!said) value = s is "visible" or "active" or "can_talk" ? 1 : 0;
                        w.CompactI(value).U8((byte)(said ? 0x02 : 0));
                    }
                    break;
                case "TriggerableBlockage":
                    w.U8(0);
                    break;
                // Functions writes nothing: its read is `ret 0` (0x1400f1180, verified in the client). Writing a count there
                // shifts every component after it — InteractionAction, MinimapIcon — and the client quits.
                case "Functions":
                    break;
                case "Brackets":     // 0 bytes, same ret-family reader as Functions
                case "Render":       // client block only
                case "Preload":      // client block only
                case "Targetable":   // client block only
                    break;
                case "InteractionAction":
                case "MinimapIcon":
                    w.U8(0);
                    break;
                // BaseEvents: server-only, nothing on the wire.
            }
        }
        w.U8(0);      // tasks
        return w.ToArray();
    }

    /// <summary>The state overrides for a type path, from <c>data/kingsmarch-states.txt</c> (<c>&lt;type&gt; state=value ...</c>,
    /// matched by the end of the type path). <c>animated=type</c> makes the Animated component 0 for that type.</summary>
    static Dictionary<string, int> Overrides(string type)
    {
        _states ??= States();
        if (_states.TryGetValue(type, out var map)) return map;
        foreach (var (key, value) in _states)
            if (type.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase) || type.Equals(key, StringComparison.OrdinalIgnoreCase))
                return value;
        return [];
    }

    static Dictionary<string, Dictionary<string, int>>? _states;

    static Dictionary<string, Dictionary<string, int>> States()
    {
        var all = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StatesFile)) return all;
            foreach (string line in File.ReadAllLines(StatesFile))
            {
                string text = line.Trim();
                if (text.Length == 0 || text[0] == '#') continue;
                var parts = text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < parts.Length; i++)
                {
                    int eq = parts[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string key = parts[i][..eq].Trim();
                    string value = parts[i][(eq + 1)..].Trim();
                    if (value.Equals("type", StringComparison.OrdinalIgnoreCase)) map[key] = 0;
                    else if (int.TryParse(value, out int n)) map[key] = n;
                }
                all[parts[0]] = map;
            }
        }
        catch (IOException) { }
        return all;
    }

    /// <summary>The town's farm crops, by the plot's <c>plant</c> state value (1..5).</summary>
    static readonly string[] Crops = ["Wheat", "Corn", "Pumpkin", "Globulora", "Orbisfera"];

    /// <summary>The mine's ores and the bar each refines into, in VillageResources order (the ore row's
    /// <c>Result</c> in VillageExports names the same pairing). The real mine digs the ore its level reaches
    /// (VillageProduction Mining1..5, one ore a level); with the town's buildings still at their base level the
    /// mine follows the farm's rotation instead, so one character can dig and ship all five.</summary>
    static readonly string[] Ores = ["IronOre", "CopperOre", "Amber", "BismuthOre", "VerisiumOre"];
    static readonly string[] Bars = ["RefinedIron", "RefinedCopper", "RefinedAmber", "RefinedBismuth", "RefinedVerisium"];

    /// <summary>How long a crop takes to ripen, in seconds.</summary>
    const double GrowSeconds = 60;
    /// <summary>One worker-cycle of the mine or the smeltery (VillageProduction Mining1/Smelting1
    /// <c>BaseProductionTime</c>; the deeper tiers are 145/180/280/480 and arrive with the building upgrades).</summary>
    const double MineSeconds = 120;
    const double SmeltSeconds = 120;
    /// <summary>What the town pays a worker for one harvest, from the treasury (Johan's Fund the Treasury).</summary>
    const int Wage = 25;
    // Seconds a plot is left alone after the client was sent it: its destroy is deferred, so removing a fresh plot again is
    // what breaks the client's object map.
    const double Quiet = 6;
    // The two town jobs a worker can hold beyond a farm plot, stored in the worker's Plot field (the saved farm keeps a
    // plot order there; these never collide with one). The mine and the smeltery have no client-side state to re-state,
    // so unlike a plot an assignment to them is only the saved worker's field.
    const int MineJob = -2;
    const int SmelterJob = -3;

    /// <summary>A click on a farm plot (C→S 0x24, skill 0x266): plants a crop on a bare plot, harvests a ripe one and says
    /// what happened. The crop is the plot object's own <c>plant</c> StateMachine state (the client swaps its model to
    /// CropsWheat01/Corn/Pumpkins/Globulora/Orbisfera for 1..5), sent as a 0x2bc component update. False = not ours.</summary>
    public static async Task<bool> Use(IPlayer player, uint objectId)
    {
        if (!string.Equals(player.AreaId, Area, StringComparison.Ordinal)) return false;
        Plot? plot;
        Progress? progress;
        BoardAction? board = null;
        lock (_lock)
        {
            _progress.TryGetValue(player, out progress);
            if (progress is not null && progress.Boards.TryGetValue(objectId, out var found)) board = found.Action;
        }
        if (board is { } pushed) return await Pushed(player, pushed);
        lock (_lock)
        {
            _progress.TryGetValue(player, out progress);
            if (progress is null || !progress.Plots.TryGetValue(objectId, out plot)) return false;
        }
        double now = Environment.TickCount64 / 1000.0;
        if (plot.Crop == 0)
        {
            // The farm panel (client screen 0x59) cannot be opened in this build, so a click chooses the crop instead: the
            // plots take turns at the five crops, so one character can grow all of them.
            int crop;
            lock (_lock) crop = 1 + progress.Rotation++ % Crops.Length;
            plot.Crop = crop;
            plot.PlantedAt = now;
            await SetPlant(player, progress, objectId, plot);
            await player.Tell($"Plot {plot.Order + 1}: planted {Crops[crop - 1]} (ripe in {GrowSeconds:0} s).");
            Persist(progress);
            return true;
        }
        double ripe = plot.PlantedAt + GrowSeconds;
        if (now < ripe)
        {
            await player.Tell($"{Crops[plot.Crop - 1]} is still growing ({ripe - now:0} s).");
            return true;
        }
        if (now - plot.CreatedAt < Quiet)
        {
            await player.Tell($"Plot {plot.Order + 1} is still settling ({plot.CreatedAt + Quiet - now:0} s).");
            return true;
        }
        string harvested = Crops[plot.Crop - 1];
        plot.Crop = 0;
        plot.Harvests++;
        lock (progress.Store) progress.Store[harvested] = progress.Store.GetValueOrDefault(harvested) + 1;
        await SetPlant(player, progress, objectId, plot);
        await player.Tell($"Plot {plot.Order + 1}: harvested {harvested} ({InStore(progress, harvested)} in the town).");
        Persist(progress);
        return true;
    }

    /// <summary>Writes this connection's farm into data/kingsmarch-farm.json: its plots' crops and what it has harvested.</summary>
    static void Persist(Progress progress)
    {
        var farm = Farm(progress.Who);
        lock (_lock)
        {
            farm.Plots = [.. progress.Plots.Values.OrderBy(p => p.Order)
                .Select(p => new SavedPlot { Crop = p.Crop, Planted = p.PlantedAt, Harvests = p.Harvests })];
            progress.Saved = farm.Plots;
            farm.Workers = [.. progress.Workers.Select(w => new SavedWorker { Plot = w.Plot, Harvests = w.Harvests, Name = w.Name })];
            farm.MineStarted = progress.MineStartedAt;
            farm.SmeltStarted = progress.SmeltStartedAt;
        }
        lock (progress.Store) farm.Store = new(progress.Store);
        SaveFarm();
    }

    static int InStore(Progress progress, string crop)
    {
        lock (progress.Store) return progress.Store.GetValueOrDefault(crop);
    }

    /// <summary>How much of a crop the town is holding — what the port needs to know before it loads a ship.</summary>
    public static int Stored(IPlayer player, string crop)
    {
        Progress progress;
        progress = State(player);
        return InStore(progress, crop);
    }

    /// <summary>Takes up to <paramref name="count"/> of a crop out of the town's store and says how many it could take.</summary>
    public static int TakeFromStore(IPlayer player, string crop, int count)
    {
        Progress progress;
        progress = State(player);
        int taken;
        lock (progress.Store)
        {
            int have = progress.Store.GetValueOrDefault(crop);
            taken = Math.Min(have, Math.Max(0, count));
            if (taken <= 0) return 0;
            if (have == taken) progress.Store.Remove(crop);
            else progress.Store[crop] = have - taken;
        }
        Persist(progress);
        return taken;
    }

    /// <summary>The town's farm as one report: each plot's crop, how long it needs, who tends it, and what is stored.</summary>
    public static string StoreText(IPlayer player)
    {
        Progress progress;
        progress = State(player);
        double clock = Environment.TickCount64 / 1000.0;
        var lines = new List<string>();
        foreach (var plot in progress.Plots.Values.OrderBy(p => p.Order))
        {
            string what = plot.Crop == 0
                ? "bare"
                : plot.PlantedAt + GrowSeconds > clock ? $"{Crops[plot.Crop - 1]}, ripe in {plot.PlantedAt + GrowSeconds - clock:0} s" : $"{Crops[plot.Crop - 1]}, ready";
            string who = string.Join(",", progress.Workers.Where(w => w.Plot == plot.Order).Select(w => w.Order + 1));
            lines.Add($"plot {plot.Order + 1}: {what}{(who.Length > 0 ? $" (worker {who})" : "")}");
        }
        var idle = progress.Workers.Where(w => w.Plot < 0).Select(w => w.Order + 1).ToList();
        lines.Add(idle.Count == 0 ? "workers: none idle" : $"workers: idle {string.Join(",", idle)}");
        var onMine = progress.Workers.Where(w => w.Plot == MineJob).Select(w => w.Order + 1).ToList();
        lines.Add("mine: " + (onMine.Count == 0 ? "no one"
            : $"worker {string.Join(",", onMine)}, next ore in {Math.Max(0, progress.MineStartedAt + MineSeconds - clock):0} s"));
        var atSmelter = progress.Workers.Where(w => w.Plot == SmelterJob).Select(w => w.Order + 1).ToList();
        lines.Add("smeltery: " + (atSmelter.Count == 0 ? "no one"
            : $"worker {string.Join(",", atSmelter)}, next bar in {Math.Max(0, progress.SmeltStartedAt + SmeltSeconds - clock):0} s"));
        var store = progress.Store.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}").ToList();
        lines.Add("stored: " + (store.Count == 0 ? "nothing yet" : string.Join(", ", store)));
        int owed = KingsmarchPort.Wages(progress.Who);
        lines.Add($"treasury: {KingsmarchPort.Gold(progress.Who)} gold, {Wage} a harvest"
            + (owed > 0 ? $", {owed} owed in wages" : ""));
        lines.Add("commands: /village hire · /village assign <worker> <plot 1-6 | mine | smelter> · /treasury [gold]");
        return string.Join(" | ", lines);
    }

    /// <summary>The town's treasury: the gold it holds, what a harvest pays, and the wages it still owes.</summary>
    public static string TreasuryText(IPlayer player)
    {
        var progress = State(player);
        int owed = KingsmarchPort.Wages(progress.Who);
        return $"{KingsmarchPort.Gold(progress.Who)} gold in the town, {Wage} a harvest"
            + (owed > 0 ? $", {owed} owed in wages" : ", no wages owed")
            + " | /treasury <gold> puts more in (Johan's Fund the Treasury adds a thousand at a time)";
    }

    /// <summary>Johan's "Fund the Treasury": gold moves from the account's stash into the town's treasury, and the wages the
    /// town still owed are settled with it first. The S→C 0x2aa that carries the new total is what the client shows.</summary>
    public static async Task<string> FundTreasury(IPlayer player, int amount)
    {
        var progress = State(player);
        var stash = Game.PersonalStashOf(Program.ConnOf(player));
        int take = (int)Math.Min(Math.Max(amount, 0), stash.Gold);
        if (take <= 0) return "the account has no gold to put into the treasury";
        uint? left = stash.TrySpendGold(take);
        if (left is null) return $"the account has {stash.Gold} gold, less than the {take} asked for";
        long treasury = KingsmarchPort.Fund(progress.Who, take);
        await player.Send(InstanceEntities.Gold(left.Value), $"0x02aa gold: {left} (funded the Kingsmarch treasury with {take})");
        int owed = KingsmarchPort.Wages(progress.Who);
        return $"Funded the treasury with {take} gold: {treasury} gold in the town{(owed > 0 ? $", {owed} still owed in wages" : ", no wages owed")}.";
    }

    /// <summary>The recruiter's pool: the Kalguur names Raulf offers, and what one hire costs in barter. The harbour and
    /// the farm share one purse, so a hire is paid for by shipping (KingsmarchPort) rather than with a second currency.</summary>
    static readonly string[] WorkerNames =
        ["Ainar", "Brynja", "Dala", "Eirik", "Fjola", "Gorm", "Haki", "Ivar", "Jorunn", "Kalf",
         "Leif", "Mira", "Njal", "Olaf", "Runa", "Sigrun", "Torfi", "Ulf", "Viggo", "Yrsa"];
    public const int RecruitCost = 40;

    /// <summary>One of the three names Raulf would offer: the same three until one is hired, so re-reading the list and
    /// then hiring the one that was read agree with each other.</summary>
    static string CandidateName(string who, int index)
    {
        int seed = index;
        foreach (char c in who) seed = (seed * 31 + c) & 0x3FFFFFFF;
        return WorkerNames[(seed + index * 7) % WorkerNames.Length];
    }

    /// <summary>The recruiter's list: three candidates, the harbour's barter and the price of a hire.</summary>
    public static string RecruitText(IPlayer player)
    {
        var progress = State(player);
        int hired;
        lock (_lock) hired = progress.Workers.Count;
        var names = Enumerable.Range(1, 3).Select(i => $"{i}) {CandidateName(progress.Who, hired + i)}");
        return $"{string.Join(", ", names)} | {KingsmarchPort.Barter(progress.Who)} barter in the harbour, "
            + $"{RecruitCost} a hire | /recruit hire <1-3>, then /village assign <worker> <plot 1-6 | mine | smelter>";
    }

    /// <summary>Hires one of the three candidates for <see cref="RecruitCost"/> barter.</summary>
    public static Task<string> Recruit(IPlayer player, int index)
    {
        var progress = State(player);
        if (index < 1 || index > 3) return Task.FromResult("usage: /recruit hire <1-3>");
        int number;
        string name;
        lock (_lock)
        {
            name = CandidateName(progress.Who, progress.Workers.Count + index);
            if (!KingsmarchPort.PayBarter(progress.Who, RecruitCost))
                return Task.FromResult($"a hire costs {RecruitCost} barter and the harbour has {KingsmarchPort.Barter(progress.Who)} "
                    + "(ship crops with /port send <port> <crop> <count>)");
            progress.Workers.Add(new Worker { Order = progress.Workers.Count, Name = name });
            number = progress.Workers.Count;
            Persist(progress);
        }
        return Task.FromResult($"Hired {name} (worker {number}, {RecruitCost} barter paid): "
            + $"/village assign {number} <plot 1-6> puts it on a plot.");
    }

    /// <summary>Hires without a name for the candidate list ("/village hire"): the first of the three, paid the same.</summary>
    public static Task<string> Hire(IPlayer player) => Recruit(player, 1);

    /// <summary>Puts a worker on a plot: the plot is re-stated with its <c>num_workers</c> count and the worker tends it from
    /// then on — planting an empty plot and harvesting a ripe one while the player is elsewhere.</summary>
    public static async Task<string> Assign(IPlayer player, int worker, int plotOrder)
    {
        Progress progress;
        progress = State(player);
        Plot plot;
        lock (_lock)
        {
            if (worker < 1 || worker > progress.Workers.Count) return $"no worker {worker}: the town has {progress.Workers.Count} (hire with /village hire)";
            plot = progress.Plots.Values.FirstOrDefault(p => p.Order == plotOrder - 1)
                ?? throw new InvalidOperationException($"no plot {plotOrder}: the town has {progress.Plots.Count}");
            progress.Workers[worker - 1].Plot = plotOrder - 1;
            Persist(progress);
        }
        await SetWorkers(player, plot, progress.Workers.Count(w => w.Plot == plotOrder - 1));
        return $"Worker {worker} now tends plot {plotOrder}.";
    }

    static async Task SetWorkers(IPlayer player, Plot plot, int count)
    {
        var states = new StateMachineData(new int[plot.States.Length], 0, plot.States).Set("num_workers", count);
        await player.Send(InstanceEntities.StateMachineUpdate(plot.ObjectId, plot.StateIndex, states),
            $"0x02bc Kingsmarch plot {plot.Order + 1} num_workers {count}");
    }

    /// <summary>Puts a worker on one of the town's two jobs — the mine (VillageJobTypes 1) or the smeltery (2) — instead
    /// of a farm plot. Neither job has a client-side plot state to re-state (the mine hut and smeltery stand as plain
    /// town furniture), so unlike <see cref="Assign"/> this only moves the worker and saves the farm.</summary>
    public static string AssignJob(IPlayer player, int worker, string job)
    {
        Progress progress = State(player);
        int slot;
        string what;
        if (job is "mine" or "mining") { slot = MineJob; what = "the mine"; }
        else if (job is "smelter" or "smelting" or "smelt" or "smeltery") { slot = SmelterJob; what = "the smeltery"; }
        else return "the town's jobs are the mine and the smeltery (a number 1-6 is a farm plot)";
        lock (_lock)
        {
            if (worker < 1 || worker > progress.Workers.Count)
                return $"no worker {worker}: the town has {progress.Workers.Count} (hire with /village hire)";
            progress.Workers[worker - 1].Plot = slot;
            Persist(progress);
        }
        return $"Worker {worker} now works {what}.";
    }

    /// <summary>The two town jobs the farm plots do not cover. A mine cycle brings one ore a digging worker up out of
    /// the rotation into the town's store; a smeltery cycle refines one ore a refining worker into its VillageExports
    /// <c>Result</c> bar, taking whatever ore the store holds (RefinedIron first). Like Work, one event a tick, and like
    /// a harvest the event's wages are charged to the treasury first — an empty treasury owes them rather than stalling
    /// the town. A smeltery cycle with no ore waits for one instead of restarting, so the next bar follows the next ore.</summary>
    static async Task TownJobs(IPlayer player, Progress progress, double clock)
    {
        int digging;
        lock (_lock) digging = progress.Workers.Count(w => w.Plot == MineJob);
        if (digging > 0 && clock - progress.MineStartedAt >= MineSeconds)
        {
            string ore;
            lock (_lock) ore = Ores[progress.MineRotation++ % Ores.Length];
            lock (progress.Store) progress.Store[ore] = progress.Store.GetValueOrDefault(ore) + digging;
            progress.MineStartedAt = clock;
            bool paid = KingsmarchPort.PayWages(progress.Who, Wage * digging);
            await player.Tell($"The mine brought up {digging} {ore} ({InStore(progress, ore)} in the town)."
                + (paid ? "" : $" ({Wage * digging} gold of wages is owed: fund the treasury with Johan.)"));
            Persist(progress);
            return;
        }
        int refining;
        lock (_lock) refining = progress.Workers.Count(w => w.Plot == SmelterJob);
        if (refining > 0 && clock - progress.SmeltStartedAt >= SmeltSeconds)
        {
            var made = new Dictionary<string, int>();
            lock (progress.Store)
            {
                for (int i = 0; i < refining; i++)
                {
                    int idx = Array.FindIndex(Ores, o => progress.Store.GetValueOrDefault(o) > 0);
                    if (idx < 0) break;
                    if (--progress.Store[Ores[idx]] == 0) progress.Store.Remove(Ores[idx]);
                    progress.Store[Bars[idx]] = progress.Store.GetValueOrDefault(Bars[idx]) + 1;
                    made[Bars[idx]] = made.GetValueOrDefault(Bars[idx]) + 1;
                }
            }
            if (made.Count == 0) return;
            progress.SmeltStartedAt = clock;
            bool paid = KingsmarchPort.PayWages(progress.Who, Wage * made.Values.Sum());
            await player.Tell($"The smeltery refined {made.Values.Sum()} ore into "
                + string.Join(", ", made.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {kv.Key}"))
                + $" ({string.Join(", ", made.Keys.Select(b => InStore(progress, b) + " " + b))} in the town)."
                + (paid ? "" : $" ({Wage * made.Values.Sum()} gold of wages is owed: fund the treasury with Johan.)"));
            Persist(progress);
        }
    }

    /// <summary>The assigned workers' round: a ripe plot is harvested into the town's store, an empty one is planted with the
    /// next crop of the rotation, so the farm keeps producing whether the player is standing there or not.</summary>
    static async Task Work(IPlayer player, Progress progress, double clock)
    {
        List<Plot> tended;
        lock (_lock) tended = [.. progress.Plots.Values.Where(p => progress.Workers.Any(w => w.Plot == p.Order)).OrderBy(p => p.Order)];
        foreach (var plot in tended)
        {
            // A plot the client has only just been sent is left alone for a while: removing it again right away is the
            // sequence that corrupts the client's object map (its 0x2bd destroy is deferred).
            if (clock - plot.CreatedAt < Quiet) continue;
            if (plot.Crop == 0)
            {
                int crop;
                lock (_lock) crop = 1 + progress.Rotation++ % Crops.Length;
                plot.Crop = crop;
                plot.PlantedAt = clock;
                await SetPlant(player, progress, plot.ObjectId, plot);
                Persist(progress);
                return;     // one plot a tick: the re-create waits for the client's deferred destroy anyway
            }
            if (clock < plot.PlantedAt + GrowSeconds) continue;
            string harvested = Crops[plot.Crop - 1];
            plot.Harvests++;
            lock (progress.Store) progress.Store[harvested] = progress.Store.GetValueOrDefault(harvested) + 1;
            // Harvested and sown with the next crop in one re-create: a bare step in between would mean two removals of the
            // same plot seconds apart, which is what killed the client on 2026-11-19 19:51 (fault 0x1ee74f3).
            int next;
            lock (_lock) next = 1 + progress.Rotation++ % Crops.Length;
            plot.Crop = next;
            plot.PlantedAt = clock;
            await SetPlant(player, progress, plot.ObjectId, plot);
            Persist(progress);
            // A harvest is what the town pays its worker for. An empty treasury does not stop the work: the wage is owed
            // and settled by the next funding (Johan's Fund the Treasury), so the farm never stalls on gold.
            bool paid = KingsmarchPort.PayWages(progress.Who, Wage);
            await player.Tell($"A worker harvested {harvested} on plot {plot.Order + 1} and sowed {Crops[next - 1]} ({InStore(progress, harvested)} in the town)."
                + (paid ? "" : $" ({Wage} gold of wages is owed: fund the treasury with Johan.)"));
            return;
        }
    }

    static async Task SetPlant(IPlayer player, Progress progress, uint objectId, Plot plot)
    {
        // The plot's crop is the model the client builds in its on_or_create_state_plant_<n> event, so a live plot changes
        // crop by being re-created in the new state (a 0x2bc update alone leaves the old model; tried 2026-11-19 17:38).
        // The new object needs a fresh id too: re-creating the same id leaves the old, bare instance in place (17:50).
        // Removing and creating in the same batch is what corrupted the client's object map (its 0x2bd destroy is
        // deferred, so the new object reusing the old block while that block is still queued breaks its tree lookup), so
        // the create waits for a later tick: see D:\大肥鱼工作区\scratch\object-lifecycle-report.md.
        var states = new StateMachineData(new int[plot.States.Length], 0, plot.States).Set("plant", plot.Crop);
        await player.Send(InstanceEntities.StateMachineUpdate(objectId, plot.StateIndex, states),
            $"0x02bc Kingsmarch plot 0x{objectId:x} plant {plot.Crop}");
        var row = plot.Row;
        int x = _origin.X + row.X, y = _origin.Y + row.Y;
        await player.Send(InstanceProtocol.RemoveObject(objectId), $"0x02bd Kingsmarch plot 0x{objectId:x} removed to replant");
        lock (_lock)
        {
            progress.Plots.Remove(objectId);
            progress.Pending.Add((Environment.TickCount64 / 1000.0 + 1.0, plot, x, y));
        }
    }

    /// <summary>Creates the plots that were removed for a replant, one second after their removal.</summary>
    static async Task Flush(IPlayer player, Progress progress, double now)
    {
        lock (_lock) if (progress.Pending.Count == 0) return;
        List<(double At, Plot Plot, int X, int Y)> due;
        lock (_lock)
        {
            due = [.. progress.Pending.Where(p => now >= p.At)];
            foreach (var p in due) progress.Pending.Remove(p);
        }
        foreach (var (_, plot, x, y) in due)
        {
            uint id;
            lock (_lock)
            {
                id = progress.Id++;
                plot.ObjectId = id;
                plot.CreatedAt = now;
                progress.Plots[id] = plot;
            }
            int hands = progress.Workers.Count(w => w.Plot == plot.Order);
            await player.Send(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(plot.Row.Type),
                    Blob(x, y, plot.Row, plot.Type, plot.Crop, hands)),
                $"0x02bb create Kingsmarch plot 0x{id:x} {plot.Row.Type} at ({x}, {y}) plant {plot.Crop} workers {hands}");
        }
    }

    static List<TownObject> Objects()
    {
        if (_objects is not null) return _objects;
        _objects = [];
        if (!File.Exists(DataFile)) return _objects;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(DataFile));
            var root = doc.RootElement;
            if (root.TryGetProperty("roomX", out var rx) && root.TryGetProperty("roomY", out var ry))
                _origin = (rx.GetInt32(), ry.GetInt32());
            foreach (var e in root.GetProperty("objects").EnumerateArray())
                _objects.Add(new TownObject(
                    e.GetProperty("type").GetString() ?? "",
                    e.GetProperty("model").GetString() ?? "",
                    e.GetProperty("x").GetInt32(),
                    e.GetProperty("y").GetInt32(),
                    (float)e.GetProperty("angle").GetDouble()));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] {DataFile}: {ex.Message}");
        }
        return _objects;
    }

    static Dictionary<string, (string[], string[])> Types()
    {
        var result = new Dictionary<string, (string[], string[])>(StringComparer.Ordinal);
        const string file = "data/object-types.json";
        if (!File.Exists(file)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                string type = t.GetProperty("type").GetString() ?? "";
                // The town also places world objects of other families (Stash, GuildStash, Waypoint, the Village NPCs);
                // they are real object types in the same table, so keep every one and let the component check decide.
                int read = t.GetProperty("read").GetInt32();
                result[type] =
                (
                    [.. t.GetProperty("components").EnumerateArray().Take(read).Select(c => c.GetString() ?? "")],
                    [.. t.GetProperty("shared_states").EnumerateArray().Select(s => s[0].GetString() ?? "")]
                );
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] {file}: {ex.Message}");
        }
        return result;
    }
}
