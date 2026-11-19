using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Harvest;

/// <summary>
/// Oshabi, Avatar of the Grove: the Sacred Blossom's grove (WorldAreas HarvestLeagueBoss, garden_boss.arm) or any other
/// place that calls <see cref="Place"/>. A soul tree (soultree.ot) stands at the encounter's point; a click emerges
/// Oshabi (OshabiBoss), whose kit the ordinary boss AI plays. At each life threshold the tree and Oshabi change colour
/// (soultree.ot: 0 none, 1 orange Sacred, 2 blue Primal, 3 green Vivid, 4 red Wild) and the colour's adds spawn while
/// she cannot be damaged. Her death sets HarvestOshabiDefeated for the players present and opens the Groveseed Altar.
/// The encounter is the instance's, saved in its content state.
/// </summary>
public static class OshabiEncounter
{
    public const string AreaId = "HarvestLeagueBoss";
    public const string TreeType = "Metadata/Terrain/Leagues/Harvest/Objects/SoulTree";
    public const string AltarType = "Metadata/MiscellaneousObjects/Descendancy/OshabiDescensionObject";
    public const string BossId = "Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss";
    public const string DefeatedFlag = "HarvestOshabiDefeated";
    const string SpawnerType = "Metadata/Terrain/Leagues/Harvest/Objects/OshabiEncounterSpawner";
    const string VineEndType = "Metadata/Terrain/Leagues/Harvest/Objects/OshabiVinePathEndMarker";
    const string SaveKey = "Harvest/oshabi";
    public const int Orange = 1, Blue = 2, Green = 3, Red = 4;
    // MonsterVarieties rows: Oshabi, Vine Hammer, Groveguard Vine, Primal Groveguard Vine, Volatile Orchard, Root Wall.
    const int BossRow = 8658, HammerVine = 8660, Groveguard = 8663, PrimalGroveguard = 8664, VolatileOrchard = 8665, RootWall = 8666;
    static readonly HashSet<int> AddRows = [HammerVine, Groveguard, PrimalGroveguard, VolatileOrchard, RootWall];
    // [G] Her Emerge: oshabiboss.ot hands the camera back 5.5 s after emerge 1. A colour change: soultree.ot's SwapTo*
    // functions end 3 s after the change.
    const double EmergeSeconds = 5.5, TransitionSeconds = 3;
    // [G] The colour changes at 75, 50 and 25% life.
    static readonly int[] Thresholds = [75, 50, 25];
    static readonly string[] Energy = ["", "energy_orange_stage", "energy_blue_stage", "energy_green_stage", "energy_red_stage"];

    /// <summary>The object types the Harvest tables load for the encounter (HarvestTables.Objects).</summary>
    internal static readonly HashSet<string> Types = new(StringComparer.Ordinal) { TreeType, AltarType };

    /// <summary>What a placement gives: the Groveseed Altar after the kill, and its log name.</summary>
    public sealed record Options(bool Altar, string Log);

    /// <summary>The encounter's saved state (the instance's content state).</summary>
    public sealed class Saved
    {
        public SacredGrove.GroveObject Tree { get; set; } = new();
        public SacredGrove.GroveObject? Altar { get; set; }
        public List<(int X, int Y)> VineEnds { get; set; } = [];
        public int Level { get; set; }
        public bool AltarOn { get; set; }
        public string Log { get; set; } = "Oshabi";
        /// <summary>The four colours in this fight's order; <see cref="Stage"/> is the current one.</summary>
        public List<int> Colours { get; set; } = [];
        public int Stage { get; set; }
        public uint Boss { get; set; }
        public bool Started { get; set; }
        public bool Emerged { get; set; }
        public bool Done { get; set; }
        public List<uint> Adds { get; set; } = [];
    }

    sealed class Encounter
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Saved? State;
        public Random Random = new();
        /// <summary>A fight runs (started, Oshabi not dead): the tick reads only this otherwise.</summary>
        public volatile bool Running;
        /// <summary>When the current colour's adds come (Clock seconds), NaN when none are due.</summary>
        public double AddsAt = double.NaN;
    }

    static readonly RuleTable<InstanceState, Encounter> Encounters = new();
    static readonly RuleTable<ClientState, HashSet<uint>> Presented = new();
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    /// <summary>Stops the running fights' steps and forgets the module's tables (module unload); the saved state brings
    /// a fight back after a reload.</summary>
    public static IDisposable CleanupOnUnload() => new Cleanup();

    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (_, e) in Encounters) e.Running = false;
            Encounters.Clear();
            Presented.Clear();
        }
    }

    static void Keep(ClientState conn, Saved state) => conn.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(state, Json);

    static Saved? Restored(ClientState conn) =>
        conn.Instance.ContentState.TryGetValue(SaveKey, out string? saved) ? JsonSerializer.Deserialize<Saved>(saved, Json) : null;

    /// <summary>GameEvents.AreaGenerated: Oshabi's own grove (the Sacred Blossom's map device area). The tree stands at the
    /// arena's OshabiEncounterSpawner point [G: garden_boss.arm has no soul tree line], else at the level's centre.</summary>
    public static async Task Enter(ClientState conn)
    {
        if (conn.Layout.Id != AreaId || MapRunOf(conn) is not { } run) return;
        var spawner = conn.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == SpawnerType);
        var at = spawner is null ? conn.Layout.Near((conn.Layout.GridW / 2, conn.Layout.GridH / 2)) : (spawner.X, spawner.Y);
        await Place(conn, at, run.Area.Level, new(Altar: true, Log: "Oshabi"));
    }

    /// <summary>Creates the instance's encounter once, with the soul tree at <paramref name="tree"/> and its monsters at
    /// <paramref name="level"/>, and shows its objects to <paramref name="conn"/>. False when the client data lacks the
    /// tree's type.</summary>
    public static async Task<bool> Place(ClientState conn, (int X, int Y) tree, int level, Options options)
    {
        if (conn.Instance is null) return false;
        var e = Encounters.GetOrCreateValue(conn.Instance);
        await e.Gate.WaitAsync();
        try
        {
            if (e.State is null && !Load(conn, e, () => Build(conn, tree, level, options))) return false;
        }
        finally { e.Gate.Release(); }
        await Present(conn, e);
        return true;
    }

    // Under the encounter's gate: the saved state, else a new one.
    static bool Load(ClientState conn, Encounter e, Func<Saved?> build)
    {
        var state = Restored(conn);
        if (state is null)
        {
            if (build() is not { } built) return false;
            state = built;
            Keep(conn, state);
        }
        e.Random = new Random(unchecked((int)(conn.Seed ^ 0x4f534842u)) ^ (state.Started ? 7919 : 0));
        e.State = state;
        e.Running = state.Started && !state.Done;
        // An emerge or a colour change cut short by a reload: its adds come at once and the gate opens again.
        if (e.Running)
            lock (conn.Monsters)
                if (!state.Emerged || conn.Monsters.TryGetValue(state.Boss, out var boss) && boss.LifePhases is { Active: true })
                    e.AddsAt = conn.Clock.Elapsed.TotalSeconds;
        return true;
    }

    static Saved? Build(ClientState conn, (int X, int Y) tree, int level, Options options)
    {
        var objects = HarvestTables.Current.Objects;
        if (!objects.ContainsKey(TreeType))
        {
            Console.WriteLine($"[harvest] {options.Log}: client.db has no {TreeType} object type (object-types v12); no encounter");
            return null;
        }
        var random = new Random(unchecked((int)(conn.Seed ^ 0x4f534842u)));
        // The fight opens in orange (Sacred): only current_colour 1 reaches the emerge effects (soultree.ot
        // on_state_current_colour_1 with oshabi_emerge 1 calls DoEmergeEffects, oshabiboss.ot's the same with emerge 1);
        // 2-4 play only SwapTo*. [G] The other three come in a random order.
        List<int> colours = [Blue, Green, Red];
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(colours));
        colours.Insert(0, Orange);
        var roomObjects = conn.Layout.Level?.RoomObjects ?? [];
        var state = new Saved
        {
            Tree = new()
            {
                Id = conn.Instance.World.NewMonsterId(), Type = TreeType, X = tree.X, Y = tree.Y,
                // The full tree (progression 9: OshabiSoulTree.ao), targetable (unlocked 1) and ready (interactable 1).
                States = { ["progression"] = 9, ["interactable"] = 1, ["unlocked"] = 1 },
            },
            VineEnds = [.. roomObjects.Where(o => o.Type == VineEndType).Select(o => (o.X, o.Y))],
            Level = Math.Clamp(level, 1, 100), AltarOn = options.Altar, Log = options.Log, Colours = colours,
        };
        if (options.Altar && objects.ContainsKey(AltarType))
        {
            var point = roomObjects.FirstOrDefault(o => o.Type == AltarType);
            var at = point is null ? conn.Layout.Near((tree.X, tree.Y + 30)) : (point.X, point.Y);
            // Hidden until Oshabi dies (OshabiDescensionObject's defaults: enabled 0, visible 0).
            state.Altar = new() { Id = conn.Instance.World.NewMonsterId(), Type = AltarType, X = at.X, Y = at.Y, Rotation = point?.Rotation ?? 0 };
        }
        Console.WriteLine($"[harvest] {options.Log}: soul tree at ({tree.X}, {tree.Y}), level {state.Level}, colours "
            + string.Join(",", colours) + $", {state.VineEnds.Count} vine ends" + (state.Altar is { } a ? $", altar at ({a.X}, {a.Y})" : ""));
        return state;
    }

    static IEnumerable<SacredGrove.GroveObject> Objects(Saved state) => state.Altar is { } altar ? [state.Tree, altar] : [state.Tree];

    static async Task Present(ClientState conn, Encounter e)
    {
        SacredGrove.GroveObject[] objects;
        string log;
        await e.Gate.WaitAsync();
        try
        {
            if (e.State is not { } state) return;
            objects = [.. Objects(state).Select(o => new SacredGrove.GroveObject
                { Id = o.Id, Type = o.Type, X = o.X, Y = o.Y, Rotation = o.Rotation, States = new(o.States) })];
            log = state.Log;
        }
        finally { e.Gate.Release(); }
        var shown = Presented.GetOrCreateValue(conn);
        foreach (var o in objects)
        {
            if (shown.Contains(o.Id) || SacredGrove.Describe(o) is not var (quest, _)) continue;
            byte[] blob = InstanceEntities.CreateObject(o.Id, InstanceEntities.ObjectTypeHash(o.Type), quest.Blob());
            await SendWorldCreate(conn, o.Id, () => blob, $"{log} {o.Type[(o.Type.LastIndexOf('/') + 1)..]} 0x{o.Id:x} at ({o.X}, {o.Y})",
                wake: true, awaitBirth: false);
            SacredGrove.Block(conn, o);
            shown.Add(o.Id);
        }
    }

    // Under the gate: sets states on the encounter's objects and sends each changed object's StateMachine to the members
    // that have it.
    static async Task SetStates(ClientState conn, Saved state, params (SacredGrove.GroveObject Object, string Name, int Value)[] changes)
    {
        var changed = new List<SacredGrove.GroveObject>();
        foreach (var (o, name, value) in changes)
        {
            o.States[name] = value;
            if (!changed.Contains(o)) changed.Add(o);
        }
        foreach (var member in SacredGrove.Members(conn))
        {
            if (!Presented.TryGetValue(member, out var shown)) continue;
            var (remote, stream, ciphers) = member.Wire;
            foreach (var o in changed)
            {
                if (!shown.Contains(o.Id) || SacredGrove.Describe(o) is not var (quest, type)) continue;
                int index = type.Components.ToList().IndexOf("StateMachine");
                await SendWorldRecord(remote, stream, ciphers, member, o.Id,
                    InstanceEntities.StateMachineUpdate(o.Id, (byte)index, quest.StateMachine!), null);
            }
        }
    }

    // Sets Oshabi's own states (oshabiboss.ot) and sends them to the members that see her; a member who creates her
    // later takes them from the instance's record.
    static async Task BossStates(ClientState conn, uint id, params (string Name, int Value)[] changes)
    {
        StateMachineData states;
        int component;
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(id, out var boss) || boss.States is not { } current || boss.Variety is not { } variety) return;
            foreach (var (name, value) in changes) current = current.Set(name, value);
            boss.States = states = current;
            if (conn.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                conn.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = states } };
            component = variety.ComponentsOrBase.ToList().IndexOf("StateMachine");
        }
        if (component < 0) return;
        foreach (var member in SacredGrove.Members(conn))
        {
            bool sees;
            lock (member.Monsters) sees = member.MonsterViews.ContainsKey(id) && !member.AsleepMonsters.Contains(id);
            if (!sees) continue;
            var (remote, stream, ciphers) = member.Wire;
            await SendWorldRecord(remote, stream, ciphers, member, id, InstanceEntities.StateMachineUpdate(id, (byte)component, states), null);
        }
    }

    /// <summary>GameEvents.ObjectUse: the soul tree starts the fight; the Groveseed Altar, once open, offers the Oshabi
    /// Bloodline.</summary>
    public static async Task<bool> Use(ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (conn.Instance is null || !Encounters.TryGetValue(conn.Instance, out var e) || e.State is not { } state) return false;
        bool tree = use.TargetId == state.Tree.Id;
        if (!tree && state.Altar?.Id != use.TargetId) return false;
        if (conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return true;
        var target = tree ? state.Tree : state.Altar!;
        var (remote, stream, ciphers) = conn.Wire;
        Task End(int action) => SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null,
            still: () => PlayerActionCurrent(conn, action));
        if (tree ? state.Started : target.States.GetValueOrDefault("enabled") == 0) { await End(conn.ActionCount); return true; }
        // [G] The tree is 17 cells across: the player stops at its edge.
        int reach = tree ? 9 : Math.Max(0, StationaryCollision.Resolve(AltarType).Size);
        if (!TryBeginObjectWalk(conn, requestedAt, target.X, target.Y, reach, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: target.Id), null,
                still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!tree)
        {
            if (PlayerActionCurrent(conn, action) && Descendancies.Find("Oshabi") is { } bloodline)
                await OpenDescendancySelector(remote, stream, ciphers, conn, bloodline.Index);
            await End(action);
            return true;
        }
        await e.Gate.WaitAsync();
        try
        {
            if (state.Started || !PlayerActionCurrent(conn, action) || conn.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(stop, (target.X, target.Y), reach)) return true;
            await Start(conn, e, state);
        }
        finally
        {
            e.Gate.Release();
            await End(action);
        }
        return true;
    }

    // Under the gate: the tree wakes and Oshabi emerges from it.
    static async Task Start(ClientState conn, Encounter e, Saved state)
    {
        if (Monsters.Find(BossRow.ToString()) is not { Supported: true } variety)
        {
            Console.WriteLine($"[harvest] {state.Log}: {BossId} cannot be written; no fight");
            return;
        }
        var (remote, stream, ciphers) = conn.Wire;
        var tree = state.Tree;
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, (byte)state.Level, MonsterRarity.Unique, tree.X, tree.Y,
            unrisen: false);
        if (id == 0) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(id, out var boss)) return;
            boss.LifePhases = new(Thresholds);
            boss.BusyUntil = Math.Max(boss.BusyUntil, now + EmergeSeconds);
            at = boss.Walk.Position(now);
        }
        state.Started = true;
        state.Boss = id;
        state.Stage = 0;
        e.Running = true;
        e.AddsAt = now + EmergeSeconds;
        Keep(conn, state);
        int first = state.Colours[0];
        bool repeat = SacredGrove.Members(conn).Any(m => ClientHasQuestFlag(m, DefeatedFlag));
        Console.WriteLine($"[harvest] {CharacterName(conn)} woke the soul tree: {state.Log} 0x{id:x} emerges (colour {first}"
            + (repeat ? ", repeat fight" : "") + ")");
        // soultree.ot: interactable 2 is the fight's start (camera and BossFightStart.ao), disable_interactables hides the
        // tree's marker, oshabi_emerge 1 plays its emerge clip.
        await SetStates(conn, state, (tree, "interactable", 2), (tree, "disable_interactables", 1), (tree, "pre_emerge_effects", 1),
            (tree, "oshabi_emerge", 1), (tree, "current_colour", first), (tree, "next_colour", state.Colours[1]));
        await BossStates(conn, id, ("visible", 1), ("emerge", 1), ("minimap_icon", 1), ("current_colour", first), ("repeat_fight", repeat ? 1 : 0));
        // Her basic action (oshabiboss.ot Actor basic_action) plays the emerge.
        if (SkillsFor(conn).ActionSkillId("Emerge") is { } emerge)
            foreach (var member in SacredGrove.Members(conn))
            {
                var w = member.Wire;
                await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, member, id,
                    InstanceProtocol.ActionStartedNow(id, at.X, at.Y, at.X, at.Y, emerge, member.NextActionSequence(), 0x0800, id), null);
            }
    }

    /// <summary>GameEvents.Tick: the colour changes, their adds, the tree's energy and Oshabi's death.</summary>
    public static async Task Tick(ClientState conn)
    {
        if (conn.Instance is null) return;
        if (!Encounters.TryGetValue(conn.Instance, out var e))
        {
            // After a module reload: the saved encounter comes back (its objects were shown before).
            if (!conn.Instance.ContentState.ContainsKey(SaveKey)) return;
            e = Encounters.GetOrCreateValue(conn.Instance);
            await e.Gate.WaitAsync();
            try
            {
                if (e.State is null && Load(conn, e, () => null))
                    foreach (var member in SacredGrove.Members(conn))
                        Presented.GetOrCreateValue(member).UnionWith(Objects(e.State!).Select(o => o.Id));
            }
            finally { e.Gate.Release(); }
        }
        if (!e.Running || conn.Closed || conn.LeavingArea || !e.Gate.Wait(0)) return;
        try
        {
            if (e.Running && e.State is { } state) await Step(conn, e, state, conn.Clock.Elapsed.TotalSeconds);
        }
        finally { e.Gate.Release(); }
    }

    static async Task Step(ClientState conn, Encounter e, Saved state, double now)
    {
        bool dead, begun = false;
        int lifePercent = 100;
        lock (conn.Monsters)
        {
            dead = !conn.Monsters.TryGetValue(state.Boss, out var boss) || boss.Dead || boss.Life == 0;
            if (!dead)
            {
                lifePercent = (int)((ulong)boss!.Life * 100 / Math.Max(1u, boss.MaxLife));
                if (double.IsNaN(e.AddsAt) && boss.LifePhases is { Active: false } phases) begun = phases.Begin(boss.Life, boss.MaxLife);
            }
        }
        if (dead) { await Death(conn, e, state); return; }
        var tree = state.Tree;
        if (!double.IsNaN(e.AddsAt))
        {
            if (now < e.AddsAt) return;
            e.AddsAt = double.NaN;
            if (!state.Emerged)
            {
                // soultree.ot: "oshabi sets it to 2 on end Emerge action".
                state.Emerged = true;
                await SetStates(conn, state, (tree, "oshabi_emerge", 2));
                await BossStates(conn, state.Boss, ("emerge", 2));
            }
            await Adds(conn, e, state, state.Colours[state.Stage]);
            lock (conn.Monsters)
                if (conn.Monsters.TryGetValue(state.Boss, out var boss)) boss.LifePhases?.Finish();
            Keep(conn, state);
            return;
        }
        if (begun)
        {
            int ended = state.Colours[state.Stage];
            state.Stage = Math.Min(state.Stage + 1, state.Colours.Count - 1);
            int colour = state.Colours[state.Stage], next = state.Stage + 1 < state.Colours.Count ? state.Colours[state.Stage + 1] : 0;
            lock (conn.Monsters)
                lock (state.Adds) state.Adds.RemoveAll(id => !conn.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0);
            e.AddsAt = now + TransitionSeconds;
            Keep(conn, state);
            Console.WriteLine($"[harvest] {state.Log} at {lifePercent}%: colour {ended} -> {colour}");
            await SetStates(conn, state, (tree, Energy[colour], 12), (tree, "current_colour", colour), (tree, "next_colour", next));
            await BossStates(conn, state.Boss, ("current_colour", colour));
            return;
        }
        // The next colour's energy fills (12 stages, one per tick) as her life falls to the next threshold.
        if (state.Stage + 1 >= state.Colours.Count || state.Stage >= Thresholds.Length) return;
        int from = state.Stage == 0 ? 100 : Thresholds[state.Stage - 1], to = Thresholds[state.Stage];
        int target = Math.Clamp((from - lifePercent) * 12 / Math.Max(1, from - to), 0, 12);
        string energy = Energy[state.Colours[state.Stage + 1]];
        int current = tree.States.GetValueOrDefault(energy);
        if (target > current) await SetStates(conn, state, (tree, energy, current + 1));
    }

    // The seed colour (HarvestTables: 1 Wild red, 2 Vivid green, 3 Primal blue) of a phase's Harvest monsters; the Sacred
    // phase has none of its own.
    static int SeedColour(int colour, Random random) => colour switch
    {
        Red => HarvestTables.Red, Green => HarvestTables.Green, Blue => HarvestTables.Blue, _ => 1 + random.Next(3),
    };

    // Under the gate: the colour's adds. [W] Sacred: a Root Wall cage around a player; Primal: Volatile Orchards;
    // Vivid: a Vine Hammer; Wild: her own kit. Every colour: two Groveguard Vines at the vine paths' ends and Harvest
    // monsters of the colour.
    static async Task Adds(ClientState conn, Encounter e, Saved state, int colour)
    {
        var random = e.Random;
        var tables = HarvestTables.Current;
        double now = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) centre;
        lock (conn.Monsters)
            centre = conn.Monsters.TryGetValue(state.Boss, out var boss) ? boss.Walk.Position(now) : (state.Tree.X, state.Tree.Y);
        var player = SacredGrove.Members(conn).FirstOrDefault(m => m.PlayerLife > 0) ?? conn;
        var near = player.Walk.Position(now);
        var life = new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
        {
            [SacredGrove.LevelMoreLife] = tables.MoreLife(state.Level),
        }.Where(kv => kv.Value != 0).ToDictionary()));
        int spawned = 0, lost = 0;
        var skipped = new HashSet<string>();
        async Task Spawn(int row, (int X, int Y) at, MonsterRarity rarity = MonsterRarity.Normal, bool established = false)
        {
            // The ticking player may take a portal mid-colour: the rest come through another player still here.
            if (SacredGrove.Members(conn).FirstOrDefault() is not { } by) { lost++; return; }
            if (Monsters.Find(row.ToString()) is not { Supported: true } variety) { skipped.Add(row.ToString()); return; }
            var (remote, stream, ciphers) = by.Wire;
            uint id = await SpawnMonster(remote, stream, ciphers, by, variety, (byte)state.Level, rarity, at.X, at.Y,
                alone: false, extraMods: life, placement: established ? MonsterPlacement.Established : MonsterPlacement.Proposed);
            if (id == 0) return;
            lock (state.Adds) state.Adds.Add(id);
            spawned++;
        }
        (int X, int Y) Around((int X, int Y) at, double radius, double angle) =>
            conn.Layout.Near(((int)Math.Round(at.X + Math.Cos(angle) * radius), (int)Math.Round(at.Y + Math.Sin(angle) * radius)));
        switch (colour)
        {
            case Orange:
                // [W] Root Walls cage the player; one must be broken to get out. Their bodies (object size 6) are the
                // cage, 7.7 cells apart on the ring: the spawn's clear-cell search would push them apart and open it.
                for (int i = 0; i < 8; i++) await Spawn(RootWall, Around(near, 10, i * Math.Tau / 8), established: true);
                break;
            case Blue:
                for (int i = 0, n = 3 + random.Next(3); i < n; i++)
                    await Spawn(VolatileOrchard, Around(centre, 15 + random.Next(15), random.NextDouble() * Math.Tau));
                break;
            case Green:
                await Spawn(HammerVine, Around(near, 8, random.NextDouble() * Math.Tau));
                break;
        }
        for (int i = 0; i < 2; i++)
            await Spawn(colour == Blue ? PrimalGroveguard : Groveguard, state.VineEnds.Count > 0
                ? state.VineEnds[random.Next(state.VineEnds.Count)] : Around(centre, 25, random.NextDouble() * Math.Tau));
        int seedColour = SeedColour(colour, random);
        for (int i = 0, n = 4 + random.Next(3); i < n; i++)
        {
            var rows = tables.ByColourTier[seedColour][1 + random.Next(2)];
            if (rows.Length == 0 || tables.SeedRow(rows[random.Next(rows.Length)]) is not { } seed) continue;
            await Spawn(seed.Monster, Around(centre, 20 + random.Next(12), random.NextDouble() * Math.Tau));
        }
        Console.WriteLine($"[harvest] {state.Log} colour {colour}: {spawned} adds"
            + (lost > 0 ? $", {lost} not spawned (no player left in the area)" : "")
            + (skipped.Count > 0 ? $", skipped unsupported varieties {string.Join(",", skipped)}" : ""));
    }

    // Under the gate: Oshabi died.
    static async Task Death(ClientState conn, Encounter e, Saved state)
    {
        e.Running = false;
        e.AddsAt = double.NaN;
        state.Done = true;
        Keep(conn, state);
        var members = SacredGrove.Members(conn).ToList();
        Console.WriteLine($"[harvest] {state.Log} defeated; {DefeatedFlag} for {string.Join(", ", members.Select(CharacterName))}");
        await SetStates(conn, state, (state.Tree, "oshabi_death", 1), (state.Tree, "reset_colour", 1));
        foreach (var member in members)
        {
            var w = member.Wire;
            await SetQuestFlags(w.Remote, w.Stream, w.Ciphers, member, [DefeatedFlag]);
        }
        if (state.AltarOn && state.Altar is { } altar)
            await SetStates(conn, state, (altar, "visible", 1), (altar, "enabled", 1));
        uint[] adds;
        // Her own kit summons Vine Hammers (the boss AI, not Adds): they go with the encounter's adds.
        lock (conn.Monsters)
            lock (state.Adds)
            {
                adds = [.. state.Adds.Union(conn.Monsters.Where(kv => !kv.Value.Dead && kv.Value.Variety?.Row is { } row && AddRows.Contains(row))
                    .Select(kv => kv.Key))];
                state.Adds.Clear();
            }
        foreach (uint id in adds)
        {
            lock (conn.Monsters)
            {
                if (conn.Monsters.Remove(id, out var m)) { m.Dead = true; m.Life = 0; m.ActionGeneration++; }
                conn.Instance.World.MonsterOrigins.TryRemove(id, out _);
            }
            foreach (var member in members)
            {
                var w = member.Wire;
                await SendWorldRemoval(w.Remote, w.Stream, w.Ciphers, member, id);
                lock (member.Monsters) { member.MonsterViews.Remove(id); member.AsleepMonsters.Remove(id); member.BossBarsSeen.Remove(id); }
            }
        }
        Keep(conn, state);
    }

    sealed record RewardTable(DropDataProvider.Snapshot Source, (string Item, double Weight)[] Uniques,
        (string Item, string Kind, double Chance)[] Others);
    static RewardTable? _rewards;

    // Oshabi's world.db drop_boss_items rows, read once per published snapshot.
    static RewardTable Rewards(DropDataProvider.Snapshot snapshot)
    {
        if (Volatile.Read(ref _rewards) is { } cached && ReferenceEquals(cached.Source, snapshot)) return cached;
        var uniques = new List<(string, double)>();
        var others = new List<(string, string, double)>();
        if (!snapshot.Bosses.TryGetValue(BossId, out var row) || row.Enabled)
            foreach (var item in snapshot.BossItems.Values)
            {
                if (item.BossId != BossId || (item.OverrideChance ?? item.Chance) is not double chance || chance <= 0) continue;
                if (item.ItemKind == "unique") uniques.Add((item.ItemId, chance));
                else others.Add((item.ItemId, item.ItemKind, Math.Min(1, chance)));
            }
        var table = new RewardTable(snapshot, [.. uniques], [.. others]);
        Volatile.Write(ref _rewards, table);
        return table;
    }

    /// <summary>GameEvents.Drops: Oshabi's rewards besides the server's own drops. Her unique rows are the shares of one
    /// guaranteed unique; every other row rolls by itself (currency as a stack). Runs per kill: outside an Oshabi
    /// instance it costs one table lookup.</summary>
    public static void Drops(DropRoll drop)
    {
        if (drop.Conn.Instance is not { } instance || !Encounters.TryGetValue(instance, out var e) || e.State is not { Boss: not 0 } state
            || drop.Monster.ObjectId != state.Boss) return;
        var table = Rewards(DropDataProvider.Default.Current);
        var random = Random.Shared;
        double total = 0;
        foreach (var (_, weight) in table.Uniques) total += weight;
        if (total > 0)
        {
            double pick = random.NextDouble() * total;
            string chosen = table.Uniques[^1].Item;
            foreach (var (item, weight) in table.Uniques)
            {
                if (pick < weight) { chosen = item; break; }
                pick -= weight;
            }
            drop.Items.Add(chosen);
        }
        foreach (var (item, kind, chance) in table.Others)
        {
            if (random.NextDouble() >= chance) continue;
            if (kind == "currency") drop.Stacks.Add((item, 1));
            else drop.Items.Add(item);
        }
        Console.WriteLine($"[harvest] {state.Log} rewards: " + string.Join(", ", drop.Items.Concat(drop.Stacks.Select(s => s.Item))));
    }
}
