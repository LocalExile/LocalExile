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
}

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
    public int MonstersPerLane;
    public readonly Dictionary<uint, (int Lane, MonsterRarity Rarity)> Monsters = [];
    public readonly Dictionary<uint, int> PathIndex = [];
    public readonly HashSet<uint> Resolved = [];
}

/// <summary>The defences of an instance.</summary>
public sealed class Run
{
    public readonly List<Defence> Defences = [];
    public readonly object Gate = new();
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

    /// <summary>The enemies of a defence: the varieties the official defence sent (the spawner objects its tables name are not
    /// MonsterVarieties rows and no table maps them to creatures) [estimated: other encounter types are not in any capture].</summary>
    static readonly string[] WaveVarieties =
    [
        "Metadata/Monsters/LeagueBlight/Cold/BlightColdWyvern", "Metadata/Monsters/LeagueBlight/Generic/Pitbull",
        "Metadata/Monsters/LeagueBlight/Cold/BlightWaterElemental", "Metadata/Monsters/LeagueBlight/Generic/Cockerel",
        "Metadata/Monsters/LeagueBlight/Generic/PitbullSlow", "Metadata/Monsters/LeagueBlight/Generic/GladiatorSword",
        "Metadata/Monsters/LeagueBlight/Generic/CockerelSlow", "Metadata/Monsters/LeagueBlight/Generic/GladiatorSwordShield",
        "Metadata/Monsters/LeagueBlight/Generic/CockerelBow",
    ];

    /// <summary>The towers that hurt an enemy, by BlightTowers Id prefix [estimated: the tower skills' own numbers are not read].</summary>
    static readonly string[] DamageTowers = ["Chilling", "Shocking", "Stunning", "Freezing", "LightningStorm", "Arcing", "Flame", "Meteor"];

    public readonly Store<InstanceState, Run> Runs = new();

    static double Frames(int frames) => frames * Program.FrameMs / 1000.0;

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Members(Player player) =>
        InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    public static byte LevelOf(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    // ---- Building the instance's defences, and showing them to whoever enters --------------------------------------------------

    /// <summary>The instance's defences, built from the level's pumps on the first entry and kept with the instance.</summary>
    public Run RunOf(Player player) => Runs.GetValue(player.Instance, _ =>
    {
        var run = new Run();
        byte level = LevelOf(player);
        bool blighted = AreaLeagueStats(player).GetValueOrDefault(Stat.IsBlightedMap) > 0;
        foreach (var o in player.Layout.Level?.RoomObjects.Where(o => o.Type == PumpObject) ?? [])
        {
            var d = new Defence(player.Instance.World.NewMonsterId(), (o.X, o.Y), o.Rotation, level)
            {
                MaxDurability = blighted ? BlightedMapDurability : MapDurability,
            };
            d.Durability = d.MaxDurability;
            d.PumpStates[PumpHealth] = d.Durability;
            d.PumpStates[PumpReadyToStart] = 1;
            run.Defences.Add(d);
        }
        return run;
    });

    /// <summary>A player enters (or comes back to) the instance: they are shown every defence as it stands.</summary>
    public async Task Enter(Player player)
    {
        var run = RunOf(player);
        List<Defence> defences;
        lock (run.Gate) defences = [.. run.Defences];
        foreach (var d in defences) await Present(player, d);
    }

    async Task Present(Player player, Defence d)
    {
        byte[] pump;
        List<Spot> spots;
        lock (d)
        {
            pump = PumpBlob(d);
            spots = [.. d.Spots.Values];
        }
        uint type = InstanceEntities.ObjectTypeHash(PumpObject);
        await SendWorldCreate(player, d.Pump, () => InstanceEntities.CreateObject(d.Pump, type, pump), $"Blight pump 0x{d.Pump:x} at {d.Cell}",
            wake: true, awaitBirth: false);
        foreach (var spot in spots) await PresentSpot(player, d, spot, announce: true);
        if (spots.Count > 0) await Send(player, InstanceBlight.Resource(d.Resource), $"0x026a Blight resource {d.Resource}");
    }

    /// <summary>The variety of a BlightTowers row (the foundation for row 0), when its create can be written.</summary>
    static MonsterCatalog.Variety? VarietyOf(int row) =>
        BlightTables.Towers.ElementAtOrDefault(row) is { Variety: >= 0 } t && Monsters.Find(t.Variety.ToString()) is { Supported: true, StateMachine: not null } v ? v : null;

    async Task PresentSpot(Player player, Defence d, Spot spot, bool announce)
    {
        if (VarietyOf(spot.Row) is not { } variety) return;
        byte level = d.Level;
        if (player.MonsterTypes.Add((variety.Row, level)) || announce)
            await Send(player, InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]), $"0x0020 monster type {variety.Id}@{level}");
        var states = SpotStates(variety, spot);
        uint life = (uint)Math.Max(1, Monsters.BaseLife(variety, level) ?? 1);
        var blob = new MonsterObject(spot.X, spot.Y, life, variety.ComponentsOrBase, StateMachine: states, TransitionState: variety.TransitionStates,
            Team: 0, Stats: [(Stat.MaximumLife, (int)life)]) { LifeData = variety.LifeData }.Blob();
        uint type = InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level));
        await SendWorldCreate(player, spot.Id, () => InstanceEntities.CreateObject(spot.Id, type, blob), $"Blight {variety.Name} 0x{spot.Id:x}",
            wake: true, awaitBirth: false);
    }

    /// <summary>A spot's StateMachine: the type's defaults, and while a tower is being raised the states the official raising sent.</summary>
    static StateMachineData SpotStates(MonsterCatalog.Variety variety, Spot spot)
    {
        var s = variety.StateMachine!;
        if (spot.Row == 0) return s;
        int step = spot.Raising;
        if (step is > 0 and < RaiseSteps) return s.Set("show_segments", step).Set("building", 1).Set("build_pending", 1);
        return s.Set("show_segments", 8).Set("building", spot.Upgrade ? 0 : 1).Set("show_effects", 1).Set("tower_active", 1);
    }

    /// <summary>
    /// The pump's create blob, component by component (the .ot order: Positioned, Pathfinding, Stats, Buffs, Life, Animated,
    /// StateMachine, Transitionable, MinimapIcon, BaseEvents, InteractionAction). Positioned has the pump's own team 1 and flag 0x01;
    /// Stats carries virtual_on_full_life 1; Life holds 1; Animated has flag 0x40 (the model is the type's); the base data is empty.
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

    // ---- Sending to everyone in the instance -----------------------------------------------------------------------------------

    static async Task Tell(Player player, uint id, byte[] bytes, string log)
    {
        foreach (var member in Members(player)) await SendWorldRecord(member, id, bytes, log);
    }

    async Task SendPumpStates(Player player, Defence d, string why)
    {
        byte[] bytes;
        lock (d) bytes = InstanceEntities.StateMachineUpdate(d.Pump, PumpStateMachine, new StateMachineData([.. d.PumpStates], 0));
        await Tell(player, d.Pump, bytes, $"Blight pump 0x{d.Pump:x} {why}");
    }

    Task SendPumpTransition(Player player, Defence d, byte to)
    {
        d.PumpTransition = to;
        return Tell(player, d.Pump, InstanceEntities.TransitionableUpdate(d.Pump, PumpTransitionable, to), $"Blight pump 0x{d.Pump:x} Transitionable {to}");
    }

    static async Task SendResource(Player player, Defence d)
    {
        var bytes = InstanceBlight.Resource(d.Resource);
        foreach (var member in Members(player)) await Send(member, bytes, $"0x026a Blight resource {d.Resource}");
    }

    // ---- Starting --------------------------------------------------------------------------------------------------------------

    /// <summary>A click on the pump. False when the object is not a defence's pump.</summary>
    public async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is null || !Runs.TryGetValue(player.Instance, out var run)) return false;
        Defence? d;
        lock (run.Gate) d = run.Defences.FirstOrDefault(x => x.Pump == use.TargetId);
        if (d is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        bool ready;
        lock (d) ready = d.Phase == Phase.Ready;
        if (!ready)
        {
            int current = player.ActionCount;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, current));
            return true;
        }
        if (!TryBeginObjectWalk(player, requestedAt, d.Cell.X, d.Cell.Y, 4, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, d.Cell.X, d.Cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: d.Pump), null,
                still: () => PlayerActionCurrent(player, action));
        try
        {
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea
                || !GameServer.Core.World.PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), d.Cell, 4)) return true;
            await Start(player, d);
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    public async Task Start(Player player, Defence d)
    {
        var rng = new Random(HashCode.Combine(player.Seed, d.Cell.X, d.Cell.Y));
        double now = AreaTime(player);
        var stats = AreaLeagueStats(player);
        lock (d)
        {
            if (d.Phase != Phase.Ready) return;
            d.Phase = Phase.Building;
            d.StartedAt = now;
            d.PumpStates[PumpReadyToStart] = 0;
            d.PumpStates[PumpActivated] = 1;
            d.PumpStep = 0;
            bool Walkable(int x, int y) => x >= 0 && y >= 0 && x < player.Layout.GridW && y < player.Layout.GridH && player.Layout.WalkableFor(x, y, 1);
            if (BlightLayout.Pick(BlightTables.Topologies, d.Level, rng) is { } topology)
                d.Plan = BlightLayout.Lay(BlightTables.Nodes, topology, d.Cell, Walkable, rng);
            int pack = 100 + stats.GetValueOrDefault(Stat.MapBlightEncounterPackSizePlusPctFinal);
            d.MonstersPerLane = Math.Max(1, BaseMonstersPerLane * pack / 100);
            d.Resource = StartResource;
            if (d.Plan is { } plan)
            {
                for (int lane = 0; lane < plan.Lanes.Count; lane++)
                    d.LaneNext[lane] = now + Frames(BuildFrames) + plan.Lanes[lane].Delay;
                foreach (var (x, y) in plan.Foundations)
                {
                    uint id = player.Instance.World.NewMonsterId();
                    d.Spots[id] = new Spot(id, x, y);
                }
            }
        }
        await SendPumpStates(player, d, "activated");
        await Tell(player, d.Pump, InstanceEntities.ComponentUpdate(d.Pump, PumpInteraction, [0]), $"Blight pump 0x{d.Pump:x} InteractionAction 0");
        foreach (var member in Members(player))
        {
            foreach (var spot in d.Spots.Values) await PresentSpot(member, d, spot, announce: true);
            await Send(member, InstanceBlight.Resource(d.Resource), $"0x026a Blight resource {d.Resource}");
        }
    }

    // ---- The tick --------------------------------------------------------------------------------------------------------------

    /// <summary>Advances every defence of the instance by one tick; <paramref name="player"/> is the one live client that runs it.</summary>
    public Task Tick(Player player) => Tick(player, AreaTime(player));

    /// <summary>One tick at the area time <paramref name="now"/> (seconds).</summary>
    public async Task Tick(Player player, double now)
    {
        if (player.Instance is null || !Runs.TryGetValue(player.Instance, out var run)) return;
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
            await RaiseTowers(player, d);
            if (d.Phase == Phase.Running)
            {
                await Spawn(player, d, now);
                await March(player, d, now);
                await FireTowers(player, d, now);
                await CheckEnd(player, d, now);
            }
            else if (now >= d.StartedAt + Frames(BuildFrames))
            {
                lock (d) d.Phase = Phase.Running;
            }
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
        List<(uint Id, SpawnedMonster M)> leaked = [];
        lock (d)
            foreach (var (id, info) in d.Monsters)
            {
                if (d.Resolved.Contains(id)) continue;
                if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0) { d.Resolved.Add(id); continue; }
                var (x, y) = m.Walk.Position(now);
                if (Math.Max(Math.Abs(x - d.Cell.X), Math.Abs(y - d.Cell.Y)) <= PumpReach) leaked.Add((id, m));
            }
        foreach (var (id, m) in leaked)
        {
            int loss = 1;
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
        await SendWorldRemoval(player, id, $"Blight monster 0x{id:x} leaves the fight");
        await SendRemainingMonsters(player);
    }

    async Task Spawn(Player player, Defence d, double now)
    {
        if (d.Plan is not { } plan) return;
        for (int lane = 0; lane < plan.Lanes.Count; lane++)
        {
            int index;
            lock (d)
            {
                if (now < d.LaneNext.GetValueOrDefault(lane) || d.LaneSpawned.GetValueOrDefault(lane) >= d.MonstersPerLane) continue;
                bool first = plan.Lanes.Select((l, i) => (l.Delay, i)).Min() is var m && m.i == lane;
                d.LaneNext[lane] = Math.Max(d.LaneNext[lane] + Frames(first ? FirstLaneFrames : OtherLaneFrames), now);
                index = d.LaneSpawned[lane] = d.LaneSpawned.GetValueOrDefault(lane) + 1;
            }
            var variety = Monsters.Find(WaveVarieties[(lane * 5 + index) % WaveVarieties.Length]);
            if (variety is not { Supported: true }) continue;
            var end = plan.Lanes[lane].End;
            uint id = await SpawnMonster(player, variety, d.Level, MonsterRarity.Normal, end.X, end.Y, log: false, alone: false, instanceOwned: true);
            if (id == 0) return;
            lock (d)
            {
                d.Monsters[id] = (lane, MonsterRarity.Normal);
                d.PathIndex[id] = plan.Lanes[lane].Path.Count - 1;
            }
        }
    }

    /// <summary>Sends each of the defence's monsters that is not fighting on to the next stretch of its lane towards the pump.</summary>
    async Task March(Player player, Defence d, double now)
    {
        if (d.Plan is not { } plan) return;
        List<(uint Id, (int X, int Y) From, (int X, int Y) To)> orders = [];
        lock (d)
            foreach (var (id, info) in d.Monsters)
            {
                if (d.Resolved.Contains(id) || !d.PathIndex.TryGetValue(id, out int index) || index <= 0) continue;
                if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0 || m.Aggro || now < m.Walk.ArrivesAt) continue;
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
            await SendWorldRecord(player, id, InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, 0x2909, 0xffff, player.MonsterOrderFlags),
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
        foreach (var (id, m) in left) await RemoveMonster(player, id, m);
        await SendPumpStates(player, d, won ? "success" : "fail");
        await SendPumpTransition(player, d, 4);
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
        foreach (uint id in go) await SendWorldRemoval(player, id, $"Blight foundation 0x{id:x} goes");
    }

    // ---- Kills -----------------------------------------------------------------------------------------------------------------

    /// <summary>A kill (the player's or a minion's): a defence's enemy gives the resource its rarity's BlightBalancePerLevel value names.</summary>
    public async Task Killed(KillContext kill)
    {
        var player = kill.Player;
        if (player.Instance is null || !Runs.TryGetValue(player.Instance, out var run)) return;
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
        var (target, row) = InstanceBlight.ReadTowerBuild(bytes);
        if (player.Instance is null || !Runs.TryGetValue(player.Instance, out var run)) return false;
        Defence? d = null;
        Spot? spot = null;
        lock (run.Gate)
            foreach (var x in run.Defences)
                lock (x)
                    if (x.Spots.TryGetValue(target, out spot)) { d = x; break; }
        if (d is null || spot is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        var tables = BlightTables.Towers;
        if (row >= tables.Count || VarietyOf((int)row) is not { } variety) return true;
        var current = tables[spot.Row];
        var stats = AreaLeagueStats(player);
        int percent = 100 + stats.GetValueOrDefault(Stat.MapBlightTowerCostPlusPct);
        long cost = (long)tables[(int)row].Cost * Math.Max(0, percent) / 100 * (stats.GetValueOrDefault(Stat.MapBlightTowerCostDoubled) > 0 ? 2 : 1);
        uint oldId;
        lock (d)
        {
            if (d.Phase is not (Phase.Building or Phase.Running) || spot.Raising != 0 || !current.Next.Contains((int)row) || d.Resource < cost) return true;
            d.Resource -= (uint)cost;
            oldId = spot.Id;
            d.Spots.Remove(oldId);
            spot.Upgrade = spot.Row > 0;
            spot.Row = (int)row;
            spot.Raising = spot.Upgrade ? RaiseSteps : 1;
            spot.Id = player.Instance.World.NewMonsterId();
            d.Spots[spot.Id] = spot;
        }
        await SendResource(player, d);
        foreach (var member in Members(player)) await PresentSpot(member, d, spot, announce: false);
        await SendWorldRemoval(player, oldId, $"Blight 0x{oldId:x} replaced by tower 0x{spot.Id:x}");
        return true;
    }

    async Task RaiseTowers(Player player, Defence d)
    {
        List<(Spot Spot, int Step, uint Id)> raising;
        lock (d) raising = [.. d.Spots.Values.Where(s => s.Raising != 0).Select(s => (s, s.Raising, s.Id))];
        foreach (var (spot, step, id) in raising)
        {
            if (VarietyOf(spot.Row) is not { } variety) continue;
            StateMachineData states;
            lock (d)
            {
                states = SpotStates(variety, spot);
                spot.Raising = step < RaiseSteps ? step + 1 : 0;
                if (spot.Raising == 0) spot.NextFire = AreaTime(player) + TowerInterval;
            }
            await Tell(player, id, InstanceEntities.StateMachineUpdate(id, SpotStateMachine, states), $"Blight tower 0x{id:x} raising {step}/{RaiseSteps}");
        }
    }

    /// <summary>Each finished damage tower hits the nearest enemy of the defence in its radius once a second. The radius is read as cells and
    /// the damage is the tower variety's own base damage [estimated: the tower skills' numbers are not read; no ailment is applied].</summary>
    async Task FireTowers(Player player, Defence d, double now)
    {
        List<(Spot Spot, BlightTables.Tower Tower, MonsterCatalog.Variety Variety)> ready = [];
        lock (d)
            foreach (var spot in d.Spots.Values)
                if (spot.Row > 0 && spot.Raising == 0 && now >= spot.NextFire
                    && BlightTables.Towers[spot.Row] is var tower && DamageTowers.Any(p => tower.Id.StartsWith(p, StringComparison.Ordinal))
                    && VarietyOf(spot.Row) is { } variety)
                    ready.Add((spot, tower, variety));
        foreach (var (spot, tower, variety) in ready)
        {
            uint targetId = 0;
            SpawnedMonster? target = null;
            double best = tower.Radius;
            lock (d)
                foreach (var id in d.Monsters.Keys)
                {
                    if (d.Resolved.Contains(id) || !player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0) continue;
                    var p = m.Walk.Position(now);
                    double distance = Math.Sqrt((double)(p.X - spot.X) * (p.X - spot.X) + (double)(p.Y - spot.Y) * (p.Y - spot.Y));
                    if (distance > best) continue;
                    best = distance;
                    (targetId, target) = (id, m);
                }
            if (target is null) continue;
            spot.NextFire = now + TowerInterval;
            int damage = Monsters.BaseDamage(variety, d.Level) is { } dmg ? (dmg.Min + dmg.Max) / 2 : 0;
            if (damage > 0) await HitMonster(player, targetId, target, damage, tower.Name, log: false);
        }
    }
}
