using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>A pump's foundation, and the tower that stands on it after a build. <see cref="Row"/> is the BlightTowers row (0: empty).</summary>
public sealed class Spot(uint id, int x, int y)
{
    public uint Id = id;
    public int X = x, Y = y, Row;
    /// <summary>The next of <see cref="BlightEncounter.RaiseSteps"/> to send; 0 when the tower stands.</summary>
    public int Raising;
    public bool Upgrade;
    public double NextFire;
    /// <summary>Living friendly monster ids summoned by a Minion/Scout/Sentinel tower on this spot.</summary>
    public readonly List<uint> Minions = [];
}

/// <summary>A spot as one send reads it: copied under the defence's lock, so a tick that raises the tower cannot change it mid-send.</summary>
public readonly record struct SpotView(uint Id, int X, int Y, int Row, int Raising, bool Upgrade);

public enum Phase { Ready, Building, Running, Won, Lost }

/// <summary>One pump defence of an instance. Its state is the instance's: whoever is in the instance sees and drives the same one.</summary>
public sealed class Defence(uint pump, (int X, int Y) cell, float rotation, byte level)
{
    public readonly uint Pump = pump;
    public readonly (int X, int Y) Cell = cell;
    public readonly float Rotation = rotation;
    public readonly byte Level = level;
    public Phase Phase;
    public int Durability, MaxDurability;
    public uint Resource;
    public double StartedAt, EndedAt = double.PositiveInfinity;
    public readonly int[] PumpStates = new int[BlightEncounter.PumpStateNames.Length];
    public byte PumpTransition = 1;
    public int PumpStep;
    public double NextPathOffAt = double.PositiveInfinity;
    public BlightLayout.Plan? Plan;
    public readonly Dictionary<uint, Spot> Spots = [];
    public readonly Dictionary<int, double> LaneNext = [];
    public readonly Dictionary<int, int> LaneSpawned = [];
    public readonly Dictionary<int, int> LaneEncounterTypes = [];
    public readonly Dictionary<int, string> LaneRewards = [];
    public readonly HashSet<int> BossLanes = [];
    public int MonstersPerLane;
    public readonly Dictionary<uint, (int Lane, MonsterRarity Rarity)> Monsters = [];
    public readonly Dictionary<uint, int> PathIndex = [];
    public readonly HashSet<uint> Resolved = [];
    /// <summary>End-of-lane Blight reward chests spawned when the defence succeeds.</summary>
    public readonly Dictionary<uint, BlightChestState> Chests = [];
    /// <summary>Timed tower control expiries (Glacial Cage, Stone Gaze petrification, Seismic stun) by (monsterId, statsKey).</summary>
    public readonly Dictionary<(uint MonsterId, string Key), double> TimedControls = [];
    /// <summary>Spawned Sister Cassia NPC entity id beside this pump (0 when not spawned).</summary>
    public uint CassiaId;
    /// <summary>The pump and its foundations were created for the area (its object records give them to whoever joins later).</summary>
    public bool Shown;
    /// <summary>The map is a Blighted one (its own lane tree, and a longer-lived pump).</summary>
    public bool Blighted;
    /// <summary>The seed the lanes were laid with: the same seed lays the same lanes again after a module reload.</summary>
    public int PlanSeed;
    /// <summary>The lane that opens first (the plan's smallest delay), set with the plan.</summary>
    public int FirstLane;
}

/// <summary>The defences of an instance.</summary>
public sealed class Run
{
    public readonly List<Defence> Defences = [];
    public readonly object Gate = new();
    /// <summary>The area time at which the instance's saved copy is next written.</summary>
    public double NextSave;
}

/// <summary>A defence as it is kept in the instance's ContentState: what a reloaded module needs to carry on (the lanes are laid again from the seed).</summary>
public sealed class SavedDefence
{
    public uint Pump, CassiaId;
    public int CellX, CellY;
    public float Rotation;
    public byte Level;
    public bool Blighted, Shown;
    public Phase Phase;
    public int Durability, MaxDurability, PumpTransition, PumpStep, MonstersPerLane, PlanSeed;
    public uint Resource;
    public double StartedAt, EndedAt = double.PositiveInfinity, NextPathOffAt = double.PositiveInfinity;
    public int[] PumpStates = [];
    public List<SavedSpot> Spots = [];
    public Dictionary<int, double> LaneNext = [];
    public Dictionary<int, int> LaneSpawned = [];
    public Dictionary<int, int> LaneEncounterTypes = [];
    public Dictionary<int, string> LaneRewards = [];
    public List<int> BossLanes = [];
    public List<SavedMonster> Monsters = [];
    public Dictionary<uint, int> PathIndex = [];
    public List<uint> Resolved = [];
    public List<BlightChestState> Chests = [];
}

public sealed class SavedSpot
{
    public uint Id;
    public int X, Y, Row, Raising;
    public bool Upgrade;
    public double NextFire;
    public List<uint> Minions = [];
}

public sealed class SavedMonster
{
    public uint Id;
    public int Lane;
    public MonsterRarity Rarity;
}

public sealed class SavedRun
{
    public List<SavedDefence> Defences = [];
}

/// <summary>
/// The Blight pump defence (CoreLeagues row 16). The client rolls it before it lays out the terrain (the generator's BlightEncounter
/// feature row, whose room places the BlightPump object), and the server generates the same level, so a defence exists exactly where
/// the instance's level holds a pump: the area's stats, its league chance and the map checks are the generator's own.
/// A click on the pump starts the build phase; the lanes of a BlightTopologies tree then open one after another and send their
/// enemies to the pump, while the towers the client lets the player build from the foundations fire at them.
/// </summary>
public sealed class BlightEncounter
{
    /// <summary>The terrain object the defence is built round, written as the capture's create has it.</summary>
    public const string PumpObject = "Metadata/Terrain/Leagues/Blight/Objects/BlightPump";

    /// <summary>The pump's shared states, in its .ot order.</summary>
    public static readonly string[] PumpStateNames =
        ["ready_to_build", "health", "ui_description", "next_path", "activated", "success", "fail", "build_step", "ready_to_start"];

    const int PumpHealth = 1, PumpNextPath = 3, PumpActivated = 4, PumpSuccess = 5, PumpFail = 6, PumpBuildStep = 7, PumpReadyToStart = 8;

    /// <summary>The pump's component order (the .ot): the indexes of the components a defence updates.</summary>
    const byte PumpStateMachine = 6, PumpTransitionable = 7, PumpInteraction = 10;

    /// <summary>A foundation's and a tower's StateMachine and Transitionable, in their component order.</summary>
    const byte SpotStateMachine = 11, SpotTransitionable = 12;

    /// <summary>The create blob of a Blight monster's wake when the pump's own states say the encounter runs.</summary>
    const uint VirtualOnFullLife = Stat.VirtualOnFullLife;

    /// <summary>How many monsters one lane sends: the official defence sent 45 to 55 down each of its 4 lanes (one capture), so 50 [estimated].</summary>
    public const int BaseMonstersPerLane = 50;

    /// <summary>The build phase: the first monster came 758 frames after the pump was started (one official defence).</summary>
    public const int BuildFrames = 758;

    /// <summary>The spawn interval per lane, in frames: the lane that opened first ran at a median of 9, every other lane at 22.</summary>
    public const int FirstLaneFrames = 9, OtherLaneFrames = 22;

    /// <summary>The empty foundations went 44 frames after the end of the official defence.</summary>
    public const int FoundationsGoFrames = 44;

    /// <summary>The pump's timeline after its start, in frames: (frame, state, value, Transitionable or -1), as the official pump sent it
    /// (build steps 1 to 9 every 3 frames from +4; a lane announced at +28 with Transitionable 2; activated 2 at +43; the lane's mark off at +58).</summary>
    static readonly (int Frame, int State, int Value, int Transition)[] PumpTimeline =
    [
        (4, PumpBuildStep, 1, -1), (7, PumpBuildStep, 2, -1), (10, PumpBuildStep, 3, -1), (13, PumpBuildStep, 4, -1), (16, PumpBuildStep, 5, -1),
        (19, PumpBuildStep, 6, -1), (22, PumpBuildStep, 7, -1), (25, PumpBuildStep, 8, -1), (28, PumpBuildStep, 9, -1), (28, PumpNextPath, 1, 2),
        (43, PumpActivated, 2, -1), (58, PumpNextPath, 0, -1), (736, -1, 0, 3),
    ];

    /// <summary>The resource a defence starts with. The first purchase of the official defence (100) left 610 and no resource message
    /// came before it [estimated: 710].</summary>
    public const uint StartResource = 710;

    /// <summary>The pump's durability: 10, 20 in a Blighted map [estimated: the tables hold no value for it].</summary>
    public const int MapDurability = 10, BlightedMapDurability = 20;

    /// <summary>Raising a new tower sends nine StateMachine updates, one per tick; an upgrade only the last.</summary>
    public const int RaiseSteps = 9;

    /// <summary>Seconds between a tower's attacks: 30 to 31 frames of the official frame length (one tower, 1.0 s).</summary>
    const double TowerInterval = 1.0;

    /// <summary>How near the pump (cells, either axis) an enemy has reached it [estimated: the pump's size is 3].</summary>
    const int PumpReach = 4;

    /// <summary>The key of the instance's saved defences in its ContentState; "[]" there means the level holds no pump.</summary>
    const string StateKey = "Blight", NoPumps = "[]";

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>How often (area seconds) a running defence is written to the instance's ContentState.</summary>
    const double SaveEvery = 5;

    public readonly Store<InstanceState, Run> Runs = new();

    static double Frames(int frames) => frames * Program.FrameMs / 1000.0;

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static List<Player> Members(Player player)
    {
        var members = InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance) && Current(c)).ToList();
        if (!members.Contains(player) && Current(player)) members.Insert(0, player);
        return members;
    }

    public static byte LevelOf(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    /// <summary>Whether this call runs in the area's frame (Owned: this thread steps the area) with the client's frames begun: a record about an
    /// object is then the area's, which the frame writes to every client once.</summary>
    static bool Framed(Player player) => player.Area.Owned && player.Connection.WorldOutput.Started;

    /// <summary>
    /// One change of the instance's shared objects. In the area's frame it is made once, whichever client makes it (the frame
    /// carries it to the whole area, so a loop over the members would write it once per member for each of them); outside
    /// the frame each current member is sent it, and <paramref name="send"/> builds its own bytes for each (the writer encrypts
    /// the buffer it is given in place).
    /// </summary>
    static async Task Shared(Player player, Func<Player, Task> send)
    {
        if (Framed(player)) { await send(player); return; }
        foreach (var member in Members(player)) await send(member);
    }

    static Task Update(Player player, uint id, Func<byte[]> make, string log) => Shared(player, member => SendWorldRecord(member, id, make(), log));

    static Task Remove(Player player, uint id, string log) => Shared(player, member => SendWorldRemoval(member, id, log));

    /// <summary>A message about no object (the resource) goes to each client by itself, built anew for each.</summary>
    static async Task Tell(Player player, Func<byte[]> make, string log)
    {
        foreach (var member in Members(player)) await Send(member, make(), log);
    }

    /// <summary>A monster type the area has not registered yet (S→C 0x20 registers "Id@level"; a create of that type needs it first): every
    /// current client is told once, each its own bytes, and one that joins later is given the area's types by the area's join.</summary>
    static async Task RegisterType(Player player, int row, byte level, string log)
    {
        if (!player.MonsterTypes.Add((row, level))) return;
        foreach (var member in Members(player)) await Send(member, InstanceEntities.MonsterTypes([((ushort)row, level)]), log);
    }

    // ---- The instance's defences, kept with the instance ----------------------------------------------------------------------

    /// <summary>The instance's defences, or null when it has none: the live ones, else the ones its ContentState keeps (a module that was
    /// reloaded carries on from them).</summary>
    Run? Of(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (Runs.TryGetValue(instance, out var run)) return run;
        if (!instance.ContentState.TryGetValue(StateKey, out var text) || text == NoPumps) return null;
        try { return Runs.GetValue(instance, _ => Restore(player, text)); }
        catch (JsonException e)
        {
            Console.WriteLine($"[blight] {player.Layout.Id}: the saved defences cannot be read ({e.Message}); the instance starts them anew");
            instance.ContentState.TryRemove(StateKey, out _);
            return null;
        }
    }

    /// <summary>Whether an area can hold a pump defence (either a map or any campaign area whose generated level places a BlightPump).</summary>
    public bool MayHaveDefence(Player player) =>
        MapDevices.IsMapArea(player.Layout.Hash)
        || Of(player) is not null
        || (player.Layout.Level?.RoomObjects.Any(o => o.Type == PumpObject) ?? false);

    /// <summary>The defences the level places: one per BlightPump object of its rooms (the client rolls the encounter before it lays
    /// out the terrain, and the server generates the same level, so the pumps are the generator's own). Works in both maps and campaign areas.</summary>
    Run? Place(Player player)
    {
        if (player.Instance is null) return null;
        var pumps = player.Layout.Level?.RoomObjects.Where(o => o.Type == PumpObject).Select(o => (o.X, o.Y, o.Rotation)).ToList() ?? [];
        if (pumps.Count == 0)
        {
            player.Instance.ContentState[StateKey] = NoPumps;
            return null;
        }
        return Place(player, pumps);
    }

    /// <summary>The instance's defences, one at each of <paramref name="pumps"/> (cell and rotation).</summary>
    public Run Place(Player player, IEnumerable<(int X, int Y, float Rotation)> pumps)
    {
        byte level = LevelOf(player);
        bool blighted = AreaLeagueStats(player).GetValueOrDefault(Stat.IsBlightedMap) > 0;
        return Runs.GetValue(player.Instance, _ =>
        {
            var run = new Run();
            foreach (var (x, y, rotation) in pumps)
            {
                var d = new Defence(player.Instance.World.TakeObjectId(), (x, y), rotation, level)
                {
                    Blighted = blighted, MaxDurability = blighted ? BlightedMapDurability : MapDurability,
                };
                d.Durability = d.MaxDurability;
                d.PumpStates[PumpHealth] = d.Durability;
                d.PumpStates[PumpReadyToStart] = 1;
                run.Defences.Add(d);
            }
            return run;
        });
    }

    Run Restore(Player player, string text)
    {
        var saved = JsonSerializer.Deserialize<SavedRun>(text, Json) ?? new();
        var run = new Run();
        foreach (var s in saved.Defences)
        {
            var d = new Defence(s.Pump, (s.CellX, s.CellY), s.Rotation, s.Level)
            {
                Phase = s.Phase, Durability = s.Durability, MaxDurability = s.MaxDurability, Resource = s.Resource, StartedAt = s.StartedAt,
                EndedAt = s.EndedAt, PumpTransition = (byte)s.PumpTransition, PumpStep = s.PumpStep, NextPathOffAt = s.NextPathOffAt,
                MonstersPerLane = s.MonstersPerLane, Shown = s.Shown, Blighted = s.Blighted, PlanSeed = s.PlanSeed, CassiaId = s.CassiaId,
            };
            Array.Copy(s.PumpStates, d.PumpStates, Math.Min(s.PumpStates.Length, d.PumpStates.Length));
            foreach (var p in s.Spots)
            {
                var spot = new Spot(p.Id, p.X, p.Y) { Row = p.Row, Raising = p.Raising, Upgrade = p.Upgrade, NextFire = p.NextFire };
                if (p.Minions is { Count: > 0 }) spot.Minions.AddRange(p.Minions);
                d.Spots[p.Id] = spot;
            }
            foreach (var (lane, at) in s.LaneNext) d.LaneNext[lane] = at;
            foreach (var (lane, n) in s.LaneSpawned) d.LaneSpawned[lane] = n;
            foreach (var (lane, et) in s.LaneEncounterTypes) d.LaneEncounterTypes[lane] = et;
            foreach (var (lane, rw) in s.LaneRewards) d.LaneRewards[lane] = rw;
            d.BossLanes.UnionWith(s.BossLanes);
            foreach (var m in s.Monsters) d.Monsters[m.Id] = (m.Lane, m.Rarity);
            foreach (var (id, i) in s.PathIndex) d.PathIndex[id] = i;
            d.Resolved.UnionWith(s.Resolved);
            foreach (var c in s.Chests) d.Chests[c.Id] = c;
            if (d.Phase != Phase.Ready) d.Plan = MakePlan(player, d);
            run.Defences.Add(d);
        }
        return run;
    }

    /// <summary>Writes the defences to the instance's ContentState.</summary>
    void Save(Player player, Run run)
    {
        if (player.Instance is not { } instance) return;
        var saved = new SavedRun();
        lock (run.Gate)
            foreach (var d in run.Defences)
                lock (d)
                    saved.Defences.Add(new SavedDefence
                    {
                        Pump = d.Pump, CassiaId = d.CassiaId, CellX = d.Cell.X, CellY = d.Cell.Y, Rotation = d.Rotation, Level = d.Level, Blighted = d.Blighted, Shown = d.Shown,
                        Phase = d.Phase, Durability = d.Durability, MaxDurability = d.MaxDurability, PumpTransition = d.PumpTransition, PumpStep = d.PumpStep,
                        MonstersPerLane = d.MonstersPerLane, PlanSeed = d.PlanSeed, Resource = d.Resource, StartedAt = d.StartedAt, EndedAt = d.EndedAt,
                        NextPathOffAt = d.NextPathOffAt, PumpStates = [.. d.PumpStates],
                        Spots = [.. d.Spots.Values.Select(s => new SavedSpot { Id = s.Id, X = s.X, Y = s.Y, Row = s.Row, Raising = s.Raising, Upgrade = s.Upgrade, NextFire = s.NextFire, Minions = [.. s.Minions] })],
                        LaneNext = new(d.LaneNext), LaneSpawned = new(d.LaneSpawned), LaneEncounterTypes = new(d.LaneEncounterTypes),
                        LaneRewards = new(d.LaneRewards), BossLanes = [.. d.BossLanes],
                        PathIndex = new(d.PathIndex), Resolved = [.. d.Resolved],
                        Monsters = [.. d.Monsters.Select(m => new SavedMonster { Id = m.Key, Lane = m.Value.Lane, Rarity = m.Value.Rarity })],
                        Chests = [.. d.Chests.Values],
                    });
        instance.ContentState[StateKey] = JsonSerializer.Serialize(saved, Json);
    }

    // ---- Showing the defences ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A player enters (or comes back to) the instance. The first one to enter creates the pump for the area, where the area keeps
    /// its object records: a party member, a portal re-entry and a return after a death are given the pump, the foundations and the
    /// towers as they stand by the area's join, and its monster types by the area's types, so nothing is created twice. What the
    /// join does not carry is the resource, which a running defence tells the entering client.
    /// </summary>
    public async Task Enter(Player player)
    {
        var run = Of(player) ?? Place(player);
        if (run is null) return;
        List<Defence> defences;
        lock (run.Gate) defences = [.. run.Defences];
        bool changed = false;
        foreach (var d in defences)
        {
            bool first;
            List<SpotView> spots;
            List<BlightChestState> chests;
            lock (d)
            {
                first = !d.Shown;
                d.Shown = true;
                spots = [.. d.Spots.Values.Select(View)];
                chests = [.. d.Chests.Values];
            }
            if (first)
            {
                changed = true;
                await ShowPump(player, d);
                await BlightCassia.SpawnNearPump(player, d);
                foreach (var spot in spots) await ShowSpot(player, d, spot);
            }
            foreach (var chest in chests)
                await BlightChests.ShowChest(player, chest, Shared, Members);
            bool running;
            lock (d) running = d.Phase != Phase.Ready;
            if (running) await SendResource(player, d);
        }
        if (changed) Save(player, run);
    }

    static SpotView View(Spot s) => new(s.Id, s.X, s.Y, s.Row, s.Raising, s.Upgrade);

    async Task ShowPump(Player player, Defence d)
    {
        byte[] blob;
        lock (d) blob = PumpBlob(d);
        uint type = InstanceEntities.ObjectTypeHash(PumpObject);
        await Shared(player, member => SendWorldCreate(member, d.Pump, () => InstanceEntities.CreateObject(d.Pump, type, blob),
            $"Blight pump 0x{d.Pump:x} at {d.Cell}", wake: true, awaitBirth: false));
    }

    /// <summary>The variety of a BlightTowers row (the foundation for row 0), when its create can be written.</summary>
    static MonsterCatalog.Variety? VarietyOf(int row) =>
        BlightTables.Towers.ElementAtOrDefault(row) is { Variety: >= 0 } t && Monsters.Find(t.Variety.ToString()) is { Supported: true, StateMachine: not null } v ? v : null;

    async Task ShowSpot(Player player, Defence d, SpotView spot)
    {
        if (VarietyOf(spot.Row) is not { } variety) return;
        byte level = d.Level;
        await RegisterType(player, variety.Row, level, $"0x0020 monster type {variety.Id}@{level}");
        var states = SpotStates(variety, spot);
        uint life = (uint)Math.Max(1, Monsters.BaseLife(variety, level) ?? 1);
        var blob = new MonsterObject(spot.X, spot.Y, life, variety.ComponentsOrBase, StateMachine: states, TransitionState: variety.TransitionStates,
            Team: 0, Stats: [(Stat.MaximumLife, (int)life)]) { LifeData = variety.LifeData }.Blob();
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
        await Shared(player, member => SendWorldCreate(member, spot.Id, () => InstanceEntities.CreateObject(spot.Id, type, blob),
            $"Blight {variety.Name} 0x{spot.Id:x}", wake: true, awaitBirth: false));
    }

    /// <summary>A spot's StateMachine: the type's defaults, and while a tower is being raised the states the official raising sent.</summary>
    static StateMachineData SpotStates(MonsterCatalog.Variety variety, SpotView spot)
    {
        var s = variety.StateMachine!;
        if (spot.Row == 0) return s;
        int step = spot.Raising;
        if (step is > 0 and < RaiseSteps) return s.Set("show_segments", step).Set("building", 1).Set("build_pending", 1);
        return s.Set("show_segments", 8).Set("building", spot.Upgrade ? 0 : 1).Set("show_effects", 1).Set("tower_active", 1);
    }

    /// <summary>
    /// The pump's create blob, component by component (the .ot order: Positioned, Pathfinding, Stats, Buffs, Life, Animated,
    /// StateMachine, Transitionable, MinimapIcon, BaseEvents, InteractionAction). Positioned has team 1 and flags 0x01, Stats carries
    /// virtual_on_full_life 1, Life holds 1, Animated has flag 0x40 (the model is the type's) and the base data is empty: each as the
    /// official pump's create has it (one capture searched; its StateMachine data and every later update of it carry flag 0 for each
    /// state, as here). The official create has two base-data entries that are not written here.
    /// </summary>
    public static byte[] PumpBlob(Defence d)
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);
        w.I32(d.Cell.X).I32(d.Cell.Y).F32(d.Rotation).U16(1).U8(1).U8(0);
        w.Stats([(VirtualOnFullLife, 1)]).U32(0).F32(3f).U8(1);
        w.U16(0);
        Pool(w, 1); Pool(w, 0); Pool(w, 0); w.U8(0);
        w.U8(0x40);
        new StateMachineData(d.PumpStates, 0).Write(w);
        w.U8(d.PumpTransition).U8(0).U8((byte)(d.PumpStates[PumpReadyToStart] != 0 && d.Phase == Phase.Ready ? 1 : 0)).U8(0);
        return w.ToArray();

        static void Pool(BlobWriter w, uint current) => w.U32(current).U32(0).U16(0).U32(0).U32(0);
    }

    // ---- Telling the instance ----------------------------------------------------------------------------------------------------

    async Task SendPumpStates(Player player, Defence d, string why)
    {
        int[] states;
        lock (d) states = [.. d.PumpStates];
        await Update(player, d.Pump, () => InstanceEntities.StateMachineUpdate(d.Pump, PumpStateMachine, new StateMachineData(states, 0)), $"Blight pump 0x{d.Pump:x} {why}");
    }

    Task SendPumpTransition(Player player, Defence d, byte to)
    {
        lock (d) d.PumpTransition = to;
        return Update(player, d.Pump, () => InstanceEntities.TransitionableUpdate(d.Pump, PumpTransitionable, to), $"Blight pump 0x{d.Pump:x} Transitionable {to}");
    }

    static Task SendResource(Player player, Defence d)
    {
        uint total;
        lock (d) total = d.Resource;
        return Tell(player, () => InstanceBlight.Resource(total), $"0x026a Blight resource {total}");
    }

    // ---- Starting --------------------------------------------------------------------------------------------------------------

    /// <summary>A click on the pump or one of its reward chests. False when the object is neither.</summary>
    public async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { } run) return false;
        Defence? d;
        lock (run.Gate) d = run.Defences.FirstOrDefault(x => x.Pump == use.TargetId);
        if (d is null) return await BlightChests.UseChest(player, run, use, requestedAt, Shared, Members, Save);
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return true;
        bool ready;
        lock (d) ready = d.Phase == Phase.Ready;
        if (!ready)
        {
            int current = player.ActionCount;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, current));
            return true;
        }
        // The pump's footprint blocks its own cells (object_types positioned_object_size 6, blocking): the player stops at its edge.
        int reach = Math.Max(0, StationaryCollision.Resolve(PumpObject).Size);
        if (!TryBeginObjectWalk(player, requestedAt, d.Cell.X, d.Cell.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, d.Cell.X, d.Cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: d.Pump), null,
                still: () => PlayerActionCurrent(player, action));
        try
        {
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), d.Cell, reach)) return true;
            await Start(player, d);
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>Lays the lanes of a defence from its seed: a topology of its level (the Blighted map's own tree in a Blighted map), laid out on
    /// walkable ground. A layout with no lane is laid again from the same draws, up to a few times; null when none has a lane.</summary>
    BlightLayout.Plan? MakePlan(Player player, Defence d)
    {
        var rng = new Random(d.PlanSeed);
        bool Walkable(int x, int y) => x >= 0 && y >= 0 && x < player.Layout.GridW && y < player.Layout.GridH && player.Layout.WalkableFor(x, y, 1);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (BlightLayout.Pick(BlightTables.Topologies, d.Level, rng, d.Blighted) is not { } topology) return null;
            var plan = BlightLayout.Lay(BlightTables.Nodes, topology, d.Cell, Walkable, rng);
            if (plan.Lanes.Count > 0)
            {
                d.FirstLane = plan.Lanes.Select((l, i) => (l.Delay, i)).Min().i;
                return plan;
            }
        }
        return null;
    }

    public async Task Start(Player player, Defence d)
    {
        double now = AreaTime(player);
        var stats = AreaLeagueStats(player);
        List<SpotView> spots;
        lock (d)
        {
            if (d.Phase != Phase.Ready) return;
            d.PlanSeed = HashCode.Combine(player.Seed, d.Cell.X, d.Cell.Y);
            if (MakePlan(player, d) is not { } plan)
            {
                // No walkable lane from this pump: the defence cannot run, and the pump stays as it was.
                Console.WriteLine($"[blight] {player.Layout.Id}: pump 0x{d.Pump:x} at {d.Cell} has no walkable lane; the defence does not start");
                return;
            }
            d.Plan = plan;
            d.Phase = Phase.Building;
            d.StartedAt = now;
            d.PumpStates[PumpReadyToStart] = 0;
            d.PumpStates[PumpActivated] = 1;
            d.PumpStep = 0;
            int pack = 100 + stats.GetValueOrDefault(Stat.MapBlightEncounterPackSizePlusPctFinal);
            d.MonstersPerLane = Math.Max(1, BaseMonstersPerLane * pack / 100);
            d.Resource = StartResource;
            var laneRng = new Random(d.PlanSeed ^ 0x517cc1b7);
            d.LaneEncounterTypes.Clear();
            foreach (var (lane, encType) in BlightWaves.PickLaneTypes(plan.Lanes.Count, laneRng))
                d.LaneEncounterTypes[lane] = encType;
            d.LaneRewards.Clear();
            foreach (var (lane, reward) in BlightChests.PickLaneRewards(plan.Lanes.Count, Program.LeagueFor(player).HardMode, laneRng))
                d.LaneRewards[lane] = reward;
            d.BossLanes.Clear();
            d.BossLanes.UnionWith(BlightWaves.PickBossLanes(plan.Lanes.Count, d.Level, d.Blighted, laneRng));
            for (int lane = 0; lane < plan.Lanes.Count; lane++)
                d.LaneNext[lane] = now + Frames(BuildFrames) + plan.Lanes[lane].Delay;
            foreach (var (x, y) in plan.Foundations)
            {
                uint id = player.Instance.World.TakeObjectId();
                d.Spots[id] = new Spot(id, x, y);
            }
            spots = [.. d.Spots.Values.Select(View)];
        }
        await SendPumpStates(player, d, "activated");
        // The official pump's InteractionAction update at the start is the component index and one byte 0.
        await Update(player, d.Pump, () => InstanceEntities.ComponentUpdate(d.Pump, PumpInteraction, [0]), $"Blight pump 0x{d.Pump:x} InteractionAction 0");
        foreach (var spot in spots) await ShowSpot(player, d, spot);
        await SendResource(player, d);
        await BlightCassia.Say(player, BlightCassia.StartVoice, Members);
        if (Of(player) is { } run) Save(player, run);
    }

    // ---- The tick --------------------------------------------------------------------------------------------------------------

    /// <summary>One frame of the area (GameEvents.AreaTick), on the connection that runs the instance's monsters.</summary>
    public async Task Step(Player player)
    {
        if (!Current(player) || player.Paused || Of(player) is null || !IsMonsterRunner(player)) return;
        await Tick(player, AreaTime(player));
    }

    /// <summary>Advances every defence of the instance by one tick; <paramref name="player"/> is the one live client that runs it.</summary>
    public Task Tick(Player player) => Tick(player, AreaTime(player));

    /// <summary>One tick at the area time <paramref name="now"/> (seconds).</summary>
    public async Task Tick(Player player, double now)
    {
        if (Of(player) is not { } run) return;
        List<Defence> defences;
        lock (run.Gate) defences = [.. run.Defences];
        foreach (var d in defences)
        {
            Phase phase;
            lock (d) phase = d.Phase;
            if (phase is Phase.Ready) continue;
            await Timeline(player, d, now);
            if (phase is Phase.Won or Phase.Lost) { await RemoveFoundations(player, d, now); continue; }
            await Settle(player, d, now);
            await RaiseTowers(player, d, now);
            if (d.Phase == Phase.Running)
            {
                await Spawn(player, d, now);
                await March(player, d, now);
                await BlightTowers.StepTowers(player, d, now, Members, VarietyOf);
                await CheckEnd(player, d, now);
            }
            else if (d.Phase == Phase.Building)
            {
                // Minion and Imbuing towers already act during the build phase once raised.
                await BlightTowers.StepTowers(player, d, now, Members, VarietyOf);
                if (now >= d.StartedAt + Frames(BuildFrames))
                    lock (d) d.Phase = Phase.Running;
            }
        }
        if (now >= run.NextSave)
        {
            run.NextSave = now + SaveEvery;
            Save(player, run);
        }
    }

    async Task Timeline(Player player, Defence d, double now)
    {
        while (true)
        {
            bool changed = false;
            int transition = -1;
            lock (d)
            {
                if (d.Phase is Phase.Won or Phase.Lost || d.PumpStep >= PumpTimeline.Length || now < d.StartedAt + Frames(PumpTimeline[d.PumpStep].Frame)) break;
                int frame = PumpTimeline[d.PumpStep].Frame;
                while (d.PumpStep < PumpTimeline.Length && PumpTimeline[d.PumpStep].Frame == frame)
                {
                    var entry = PumpTimeline[d.PumpStep++];
                    if (entry.State >= 0 && d.PumpStates[entry.State] != entry.Value) { d.PumpStates[entry.State] = entry.Value; changed = true; }
                    if (entry.Transition >= 0) transition = entry.Transition;
                }
            }
            if (changed) await SendPumpStates(player, d, "timeline");
            if (transition >= 0) await SendPumpTransition(player, d, (byte)transition);
        }
    }

    /// <summary>Settles the defence's monsters: a dead one is resolved, one at the pump leaks and costs durability.</summary>
    async Task Settle(Player player, Defence d, double now)
    {
        List<(uint Id, SpawnedMonster M, MonsterRarity Rarity)> leaked = [];
        lock (d)
            foreach (var (id, info) in d.Monsters)
            {
                if (d.Resolved.Contains(id)) continue;
                if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0) { d.Resolved.Add(id); continue; }
                var (x, y) = m.Walk.Position(now);
                if (Math.Max(Math.Abs(x - d.Cell.X), Math.Abs(y - d.Cell.Y)) <= PumpReach) leaked.Add((id, m, info.Rarity));
            }
        foreach (var (id, m, rarity) in leaked)
        {
            int loss = rarity == MonsterRarity.Unique ? BlightWaves.BossDurabilityLoss : 1;
            lock (d)
            {
                if (d.Phase is Phase.Won or Phase.Lost || !d.Resolved.Add(id)) continue;
                d.Durability = Math.Max(0, d.Durability - loss);
                d.PumpStates[PumpHealth] = d.Durability;
            }
            await RemoveMonster(player, id, m);
            await SendPumpStates(player, d, $"health {d.Durability}");
            if (d.Durability == 0) { await Finish(player, d, now, won: false); return; }
        }
    }

    /// <summary>Takes a wave monster out of the instance as one that left the fight: no death, no drops, no experience.</summary>
    static async Task RemoveMonster(Player player, uint id, SpawnedMonster m)
    {
        using (ProjectileMutation(player))
        {
            if (m.Dead || !player.Area.Monsters.ContainsKey(id)) return;
            m.Dead = true;
            m.Aggro = false;
            m.Life = 0;
            m.ActionGeneration++;
            player.Area.Monsters.Remove(id);
        }
        await Remove(player, id, $"Blight monster 0x{id:x} leaves the fight");
        await SendRemainingMonsters(player);
    }

    async Task Spawn(Player player, Defence d, double now)
    {
        if (d.Plan is not { } plan) return;
        var rng = AreaRng(player);
        for (int lane = 0; lane < plan.Lanes.Count; lane++)
        {
            int index, encType, totalPerLane;
            bool hasBoss;
            lock (d)
            {
                if (now < d.LaneNext.GetValueOrDefault(lane) || d.LaneSpawned.GetValueOrDefault(lane) >= d.MonstersPerLane) continue;
                d.LaneNext[lane] = Math.Max(d.LaneNext[lane] + Frames(lane == d.FirstLane ? FirstLaneFrames : OtherLaneFrames), now);
                index = d.LaneSpawned.GetValueOrDefault(lane) + 1;
                encType = d.LaneEncounterTypes.GetValueOrDefault(lane, 0);
                totalPerLane = d.MonstersPerLane;
                hasBoss = d.BossLanes.Contains(lane);
            }
            var rarity = BlightWaves.RarityFor(index, totalPerLane, hasBoss, rng);
            if (BlightWaves.VarietyFor(encType, lane, index, rarity) is not { } variety)
            {
                // None of the lane's varieties can be created: the lane has nothing to send.
                lock (d) d.LaneSpawned[lane] = d.MonstersPerLane;
                continue;
            }
            var end = plan.Lanes[lane].End;
            uint id = await SpawnMonster(player, variety, d.Level, rarity, end.X, end.Y, log: rarity == MonsterRarity.Unique, alone: false, instanceOwned: true);
            // A spawn that did not happen is not counted: the lane tries again at its next interval, and the other lanes go on.
            if (id == 0) continue;
            if (rarity == MonsterRarity.Unique && player.Area.Monsters.TryGetValue(id, out var boss)
                && BlightWaves.BossEncounterStats(d.Level) is { } bossStats)
                boss.EncounterStats = bossStats;
            lock (d)
            {
                d.LaneSpawned[lane] = index;
                d.Monsters[id] = (lane, rarity);
                d.PathIndex[id] = plan.Lanes[lane].Path.Count - 1;
            }
        }
    }

    /// <summary>Sends each of the defence's monsters that is not fighting or immobilized on to the next stretch of its lane towards the pump.</summary>
    async Task March(Player player, Defence d, double now)
    {
        if (d.Plan is not { } plan) return;
        List<(uint Id, (int X, int Y) From, (int X, int Y) To)> orders = [];
        lock (d)
            foreach (var (id, info) in d.Monsters)
            {
                if (d.Resolved.Contains(id) || !d.PathIndex.TryGetValue(id, out int index) || index <= 0) continue;
                if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0 || m.Aggro || now < m.Walk.ArrivesAt) continue;
                // Frozen, petrified, caged or stunned monsters do not march until the control ends.
                if (m.Ailments.ActionFactor(now) <= 0 || m.Effects[Stat.ActionSpeedMinusPct] >= 100) continue;
                var path = plan.Lanes[info.Lane].Path;
                var from = m.Walk.Position(now);
                int next = Math.Max(0, Math.Min(index, path.Count - 1) - 8);
                while (next > 0 && Math.Max(Math.Abs(path[next].X - from.X), Math.Abs(path[next].Y - from.Y)) < 3) next = Math.Max(0, next - 8);
                d.PathIndex[id] = next;
                m.ActionGeneration++;
                m.Walk.Order(path[next].X, path[next].Y, now);
                orders.Add((id, from, path[next]));
            }
        foreach (var (id, from, to) in orders)
            await Update(player, id, () => InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, 0x2909, 0xffff, player.MonsterOrderFlags),
                "Blight enemy walks its lane");
    }

    async Task CheckEnd(Player player, Defence d, double now)
    {
        bool done;
        lock (d)
            done = d.Plan is { } plan && plan.Lanes.Count > 0 && Enumerable.Range(0, plan.Lanes.Count).All(l => d.LaneSpawned.GetValueOrDefault(l) >= d.MonstersPerLane)
                && d.Monsters.Keys.All(d.Resolved.Contains);
        if (done) await Finish(player, d, now, won: true);
    }

    async Task Finish(Player player, Defence d, double now, bool won)
    {
        List<(uint Id, SpawnedMonster M)> left = [];
        lock (d)
        {
            if (d.Phase is Phase.Won or Phase.Lost) return;
            d.Phase = won ? Phase.Won : Phase.Lost;
            d.EndedAt = now;
            d.PumpStates[PumpNextPath] = 0;
            d.PumpStates[PumpActivated] = 3;
            d.PumpStates[won ? PumpSuccess : PumpFail] = 1;
            d.NextPathOffAt = double.PositiveInfinity;
            foreach (var id in d.Monsters.Keys.Where(id => !d.Resolved.Contains(id)).ToArray())
            {
                d.Resolved.Add(id);
                if (player.Area.Monsters.TryGetValue(id, out var m)) left.Add((id, m));
            }
        }
        await BlightTowers.CleanupDefence(player, d, Members);
        foreach (var (id, m) in left) await RemoveMonster(player, id, m);
        await SendPumpStates(player, d, won ? "success" : "fail");
        await SendPumpTransition(player, d, 4);
        if (won) await BlightChests.SpawnVictoryChests(player, d, Shared, Members);
        await BlightCassia.Say(player, won ? BlightCassia.VictoryVoice : BlightCassia.FailureVoice, Members);
        if (Of(player) is { } run) Save(player, run);
    }

    async Task RemoveFoundations(Player player, Defence d, double now)
    {
        List<uint> go;
        lock (d)
        {
            if (now < d.EndedAt + Frames(FoundationsGoFrames)) return;
            go = [.. d.Spots.Where(s => s.Value.Row == 0).Select(s => s.Key)];
            foreach (uint id in go) d.Spots.Remove(id);
        }
        foreach (uint id in go) await Remove(player, id, $"Blight foundation 0x{id:x} goes");
        if (go.Count > 0 && Of(player) is { } run) Save(player, run);
    }

    // ---- Kills -----------------------------------------------------------------------------------------------------------------

    /// <summary>A kill (the player's or a minion's): a defence's enemy gives the resource its rarity's BlightBalancePerLevel value names.</summary>
    public async Task Killed(KillContext kill)
    {
        var player = kill.Player;
        if (Of(player) is not { } run) return;
        Defence? d = null;
        int gained = 0;
        lock (run.Gate)
            foreach (var x in run.Defences)
                lock (x)
                    if (x.Monsters.TryGetValue(kill.Monster.ObjectId, out var info) && x.Resolved.Add(kill.Monster.ObjectId))
                    {
                        d = x;
                        gained = (int)info.Rarity < BlightTables.KillResource.Count ? BlightTables.KillResource[(int)info.Rarity] : 0;
                        x.Resource += (uint)gained;
                        break;
                    }
        if (d is not null && gained > 0) await SendResource(player, d);
    }

    // ---- Towers ----------------------------------------------------------------------------------------------------------------

    /// <summary>The client's build or upgrade choice (C→S 0x265). True when the message was a defence's.</summary>
    public async Task<bool> Build(Player player, byte[] bytes)
    {
        // The packet table frames the message at its length; a shorter one is not read.
        if (bytes.Length < InstanceBlight.TowerBuildLength) return true;
        var (target, row) = InstanceBlight.ReadTowerBuild(bytes);
        if (Of(player) is not { } run) return false;
        Defence? d = null;
        lock (run.Gate)
            foreach (var x in run.Defences)
                lock (x)
                    if (x.Spots.ContainsKey(target)) { d = x; break; }
        if (d is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        var tables = BlightTables.Towers;
        if (row >= tables.Count || VarietyOf((int)row) is null) return true;
        var stats = AreaLeagueStats(player);
        int percent = 100 + stats.GetValueOrDefault(Stat.MapBlightTowerCostPlusPct);
        long cost = (long)tables[(int)row].Cost * Math.Max(0, percent) / 100 * (stats.GetValueOrDefault(Stat.MapBlightTowerCostDoubled) > 0 ? 2 : 1);
        uint oldId;
        SpotView built;
        Spot upgradedSpot;
        lock (d)
        {
            if (!d.Spots.TryGetValue(target, out var spot)) return true;
            var current = tables[spot.Row];
            if (d.Phase is not (Phase.Building or Phase.Running) || spot.Raising != 0 || !current.Next.Contains((int)row) || d.Resource < cost) return true;
            d.Resource -= (uint)cost;
            oldId = spot.Id;
            d.Spots.Remove(oldId);
            spot.Upgrade = spot.Row > 0;
            spot.Row = (int)row;
            spot.Raising = spot.Upgrade ? RaiseSteps : 1;
            spot.Id = player.Instance.World.TakeObjectId();
            d.Spots[spot.Id] = spot;
            built = View(spot);
            upgradedSpot = spot;
        }
        await BlightTowers.DismissSpotMinions(player, d, upgradedSpot);
        await SendResource(player, d);
        await ShowSpot(player, d, built);
        await Remove(player, oldId, $"Blight 0x{oldId:x} replaced by tower 0x{built.Id:x}");
        Save(player, run);
        return true;
    }

    async Task RaiseTowers(Player player, Defence d, double now)
    {
        List<(SpotView View, MonsterCatalog.Variety Variety)> raising = [];
        lock (d)
            foreach (var spot in d.Spots.Values.Where(s => s.Raising != 0))
                if (VarietyOf(spot.Row) is { } variety) raising.Add((View(spot), variety));
        foreach (var (view, variety) in raising)
        {
            StateMachineData states;
            lock (d)
            {
                if (!d.Spots.TryGetValue(view.Id, out var spot) || spot.Raising != view.Raising) continue;
                states = SpotStates(variety, View(spot));
                spot.Raising = view.Raising < RaiseSteps ? view.Raising + 1 : 0;
                // The tower's first attack/summon comes shortly after it is up, on the tick's own clock.
                if (spot.Raising == 0) spot.NextFire = now + TowerInterval;
            }
            await Update(player, view.Id, () => InstanceEntities.StateMachineUpdate(view.Id, SpotStateMachine, states),
                $"Blight tower 0x{view.Id:x} raising {view.Raising}/{RaiseSteps}");
        }
    }
}
