using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Modules.Ritual;

/// <summary>
/// An instance's Ritual: the altars, their guards, the monsters tied to them, the start, the revived waves, the daemon, the
/// tribute and the end. The bodies of the objects and the messages are <see cref="RitualWire"/>.
/// <para>
/// The altars are rolled and placed once when the instance's monsters are placed, and belong to the instance, not to a
/// player: every client of the instance sees and drives the same altars, one who enters later is given the area's objects as
/// the others have them (the area's object records) and the messages again (<see cref="EnterMessages"/>,
/// <see cref="Entered"/>), and nothing is torn down when a player leaves. The area's tick, on the connection that runs the
/// instance's monsters, steps them.
/// </para>
/// <para>
/// Times are seconds summed from the area tick; a capture frame is 1/30 s (<see cref="RitualTables.Frame"/>). Every object
/// goes out only on a cell inside the area's grid (the client crashed on an object outside the map), and a natural Ritual
/// sends nothing until a player comes near an altar.
/// </para>
/// </summary>
public sealed class RitualEncounter
{
    public const string InteractableType = "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable";
    public const string ObjectType = "Metadata/Terrain/Leagues/Ritual/RitualRuneObject";
    public const string LightType = "Metadata/Terrain/Leagues/Ritual/RitualRuneLight";
    public const string PoolType = "Metadata/Terrain/Leagues/Ritual/BloodPool";
    public const string BlockerType = "Metadata/Terrain/Leagues/Ritual/RitualBlocker";

    /// <summary>An altar's state, as 0x165 carries it: the .ot's "0 = inactive (monsters around), 1 = ready", 2 running,
    /// 3 done [capture].</summary>
    public const int Inactive = 0, Ready = 1, Running = 2, Done = 3;

    /// <summary>CoreLeagues row 20 (Ritual) is the bit of map_disable_leagues; the client's roll reads it.</summary>
    const uint RollSalt = 0x52697475;

    /// <summary>The seconds between two looks at the area's monsters for ones that stand in an inactive altar's circle.</summary>
    const double ScanSeconds = 0.5;

    /// <summary>The seconds between two tests of whether a player came near a hidden altar.</summary>
    const double RevealSeconds = 0.25;

    public sealed class Altar
    {
        public required (int X, int Y) Cell;
        public required float Rotation;
        public required RitualTables.Rune Rune;
        /// <summary>The guard packs' monsters (MonsterVarieties rows), spawned when the altar shows.</summary>
        public required List<List<int>> Guards;
        public uint Interactable, Object, Light;
        public int State;
        public bool Shown;
        public readonly HashSet<uint> Tied = [];
        public readonly HashSet<uint> Dead = [];
        public readonly List<(string Path, MonsterRarity Rarity)> Credit = [];
        public readonly List<uint> Pools = [];
        public readonly List<(uint Pool, double At)> Faded = [];
    }

    public sealed class Revived
    {
        public required string Path;
        public required MonsterRarity Rarity;
        public required int N;
    }

    public sealed class Wave
    {
        public required double At;
        public int Spawned;
        public readonly List<uint> Ids = [];
    }

    public sealed class Run
    {
        public required int Altar;
        public required double StartedAt;
        public required List<Revived> Monsters;
        public required List<(double X, double Y)> Order;
        public required Random Rng;
        public int Next;
        public readonly List<Wave> Waves = [];
        public readonly Dictionary<uint, Revived> Alive = [];
        public readonly List<uint> Daemons = [];
        public int DaemonsDue;
        public uint Blocker;
        public int Killed;
    }

    public sealed class State
    {
        public readonly List<Altar> Altars = [];
        public int Seed;
        public int Level;
        public int Completed;
        public int Tribute;
        /// <summary>The area's map_ritual_tribute_+%.</summary>
        public int TributePct;
        public double Now;
        public double ScanAt;
        public double RevealAt;
        public Run? Run;
        public List<Revived> Revived = [];
        /// <summary>The tied monsters: their altar and what comes back when it starts.</summary>
        public readonly Dictionary<uint, (int Altar, string Path, MonsterRarity Rarity)> Tie = [];
        public readonly HashSet<uint> Seen = [];
        public readonly HashSet<uint> Mine = [];
        public bool Done;
        /// <summary>One tick, one click or one entry at a time.</summary>
        public readonly SemaphoreSlim Gate = new(1, 1);
        /// <summary>The area the objects are in: a kept instance generated again has none of them (<see cref="Sync"/>).</summary>
        public GameArea? Area;
    }

    readonly ConditionalWeakTable<InstanceState, State> _states = new();

    /// <summary>The module's unload: the encounters go with it (the objects stay in their areas).</summary>
    public void Clear() => _states.Clear();

    /// <summary>The player's instance's Ritual, or null when it placed none.</summary>
    public State? Of(Player player) => player.Instance is { } instance && _states.TryGetValue(instance, out var state) ? state : null;

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Clients(Player player) =>
        InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    /// <summary>A message that is the area's, not an object's (0x165, 0x166, 0x167, 0xdf), to every client of the instance.</summary>
    static async Task Broadcast(Player player, byte[] message, string log)
    {
        var members = Clients(player).ToList();
        if (!members.Contains(player)) members.Insert(0, player);
        for (int i = 0; i < members.Count; i++) await Send(members[i], message.ToArray(), i == 0 ? log : null);
    }

    // ---- the roll and the placement -------------------------------------------------------------------------------------

    /// <summary>The test chance <c>Game_LEAGUE_CHANCES=Ritual=N</c> gives the Ritual row, as <see cref="LeagueChances"/> reads it.</summary>
    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Ritual", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    /// <summary>
    /// Whether the area has Ritual, by the client's own roll for CoreLeagues row 20 (the level generator's league roll,
    /// <see cref="ExtraTerrainFeatures.LeagueCount"/>): the area's WorldAreaLeagueChances chance raised by
    /// <c>map_league_base_chance_+%_final</c>, <c>map_league_content_additional_chance_%</c>, the row's
    /// <c>map_area_ritual_additional_chance_%</c> and <c>map_ritual_encounter_chance_+%</c>, off by
    /// <c>map_disable_leagues</c>' bit, <c>atlas_keystone_no_extra_content</c>, <c>map_nuke_everything</c>,
    /// <c>map_area_underlevelled</c> and <c>map_disable_ritual_from_chance</c>, forced by <c>map_area_contains_rituals</c>.
    /// <c>Game_LEAGUE_CHANCES=Ritual=N</c> is a test's base chance. The stats are the instance's own.
    /// </summary>
    static (int Count, string Roll) Roll(AreaPopulated area, IReadOnlyDictionary<uint, int> stats)
    {
        var player = area.Player;
        var rng = new Random(unchecked(area.Seed ^ (int)RollSalt));
        if (ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")) is int forced)
            return (LeagueChances.Current.Count(player.Layout.Id, LeagueChances.Ritual, rng), $"Game_LEAGUE_CHANCES {forced}%");
        AreaInfo info;
        try { info = AreaInfo.Load(GamePack.Default.TryRead, player.Layout.Id, area.Level); }
        catch (Exception e) when (e is ArgumentException or IOException or KeyNotFoundException)
        {
            return (0, $"area row not read ({e.GetType().Name})");
        }
        var byId = stats.ToDictionary(s => (int)s.Key, s => s.Value);
        return (ExtraTerrainFeatures.LeagueCount(info with { Stats = byId }, new LevelRandom(unchecked((uint)area.Seed ^ RollSalt), false),
            LeagueChances.Ritual), "the area's roll");
    }

    /// <summary>
    /// A new instance's monsters are placed (<see cref="GameEvents.AreaPopulated"/>, once per instance): the roll, then the
    /// altars on <see cref="AreaPopulation.EncounterSpot"/> cells, each as far from the arrival and the packs as the other
    /// encounters keep, and from the other altars by <see cref="RitualTables.SpacingMin"/> where the area has room, else by
    /// their circles. Three to four altars at a level, four with map_ritual_encounter_count_is_four. The entry sends 0x167 = 0
    /// then 0x166 (tribute 0, none done). An altar shows when a player comes near. Returns why none was placed, null when
    /// some were.
    /// </summary>
    public string? Populate(AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance) return "no instance";
        if (_states.TryGetValue(instance, out _)) return "the instance has its Ritual already";
        var layout = player.Layout;
        if (!MapDevices.IsMapArea(layout.Hash)) return "not a map";
        if (layout.Walkable is not { } walkable || !RitualTables.Loaded) return "no walk map or no Ritual tables";
        var stats = instance.KeptAreaStats ?? AreaLeagueStats(player) ?? new Dictionary<uint, int>();
        int S(uint name) => stats.GetValueOrDefault(name);
        var (count, roll) = Roll(area, stats);
        if (count < 1) return $"the roll gave none ({roll})";
        // The encounter's own draws, apart from the packs' (the same instance gets the same altars).
        var rng = new Random(area.Seed ^ (int)RollSalt);
        int level = Math.Clamp((int)area.Level, 1, 100);
        var (lo, hi) = RitualTables.AltarRange(level);
        if (S(Stat.MapRitualEncounterCountIsFour) > 0) (lo, hi) = (4, 4);
        var rune = RitualTables.PickRune(level, rng);
        if (rune is null) return "no rune for the level";
        var taken = area.Taken.ToList();
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        int wanted = rng.Next(lo, hi + 1);
        var cells = new List<(int X, int Y)>();
        for (int attempt = 0; attempt < wanted * 24 && cells.Count < wanted; attempt++)
        {
            if (AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY), transitions, taken, rng)
                is not { } at) break;
            // The first half of the draws keep the captured spacing; a small area then settles for two circles.
            double apart = attempt < wanted * 12 ? RitualTables.SpacingMin : 2 * RitualTables.RadiusCells;
            if (cells.Any(c => Distance(c, at) < apart)) continue;
            cells.Add(at);
            taken.Add(at);
        }
        if (cells.Count == 0) return "no open ground for an altar";
        var state = new State { Seed = area.Seed, Level = level, TributePct = S(Stat.MapRitualTributePlusPct), Area = player.Area };
        // Every altar of an area has the same rune [capture: two areas].
        foreach (var cell in cells) state.Altars.Add(MakeAltar(cell, rune, level, rng));
        _states.AddOrUpdate(instance, state);
        Console.WriteLine($"[ritual] {layout.Id}: {roll} gave {count}; level {level}, {state.Altars.Count} altar(s), rune {rune.Id}");
        return null;
    }

    /// <summary>The entry's first messages for the instance's Ritual (<see cref="GameEvents.EnterMessages"/>: before the
    /// area's objects): 0x167 the tribute, then 0x166 the favours window [capture: 0x167 then 0x166 at the area's start].
    /// The first client's come from <see cref="Placed"/>, as the Ritual does not exist before the area is populated.</summary>
    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player) =>
        Of(player) is { Altars.Count: > 0 } state ? Entry(state) : [];

    static IEnumerable<(string Log, byte[] Message)> Entry(State state)
    {
        yield return ($"0x167 Ritual tribute {state.Tribute}", RitualWire.Tribute(state.Tribute));
        yield return ($"0x166 Ritual favours ({state.Completed}/{state.Altars.Count} done)",
            RitualWire.Favours(state.Tribute, RitualTables.FavoursRows(state.Completed), state.Altars.Count, state.Completed));
    }

    /// <summary>The entry messages for the populating client, once the Ritual is placed.</summary>
    public async Task Placed(Player player)
    {
        if (Of(player) is not { } state) return;
        foreach (var (log, message) in Entry(state)) await Send(player, message, log);
    }

    static Altar MakeAltar((int X, int Y) cell, RitualTables.Rune rune, int level, Random rng)
    {
        var pool = RitualTables.GuardPacks(level);
        var guards = new List<List<int>>();
        int packs = pool.Count == 0 ? 0 : rng.Next(RitualTables.GuardPacksMin, RitualTables.GuardPacksMax + 1);
        for (int g = 0; g < packs; g++) guards.Add(RitualTables.RollPack(pool[rng.Next(pool.Count)], rng));
        return new Altar { Cell = cell, Rotation = (float)(rng.NextDouble() * Math.Tau), Rune = rune, Guards = guards };
    }

    // ---- entering and the re-created area -----------------------------------------------------------------------------------

    /// <summary>A player entered the instance (the first time, again by portal or after a death, or as a second party
    /// member): the area's objects reach them through its object records and the area's monster types with its join; what
    /// is theirs to be told again is each altar's latest 0x165 and the environment override while a ritual runs. A kept
    /// instance generated again is the area that has none of the objects, and gets them here.</summary>
    public async Task Entered(Player player)
    {
        if (Of(player) is not { } state || !Current(player)) return;
        await state.Gate.WaitAsync();
        try
        {
            await Sync(player, state);
            foreach (var altar in state.Altars)
                if (altar.Shown && altar.Interactable != 0 && altar.State >= Ready)
                    await Send(player, RitualWire.AltarState(altar.Interactable, altar.Cell, altar.State), $"0x165 Ritual altar 0x{altar.Interactable:x} state {altar.State}");
            if (state.Run is { } run && state.Altars[run.Altar].Rune.Environment is int env)
                await Send(player, InstanceEnvironments.Data(((ushort)env, 0)), $"0xdf Ritual environment {env} on");
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>The objects the encounter holds, made again in an area that is not the one they were made in, as they stand
    /// now. The monsters of the old area are gone with it: the run's that are not in the new one leave the run without
    /// tribute.</summary>
    async Task Sync(Player player, State state)
    {
        if (ReferenceEquals(state.Area, player.Area)) return;
        bool again = state.Area is not null;
        state.Area = player.Area;
        if (!again) return;
        foreach (var altar in state.Altars)
        {
            if (!altar.Shown || altar.Interactable == 0) continue;
            await CreateAltar(player, altar);
            foreach (uint pool in altar.Pools.Concat(altar.Faded.Select(f => f.Pool)))
                await CreatePool(player, altar, pool, altar.Cell);
        }
        // The old area's monsters are not dead: they leave the altars' ties, so none credits an altar.
        foreach (var (id, (k, _, _)) in state.Tie.Where(t => !MonsterShown(player, t.Key)).ToList())
        {
            state.Tie.Remove(id);
            state.Altars[k].Tied.Remove(id);
            state.Altars[k].Dead.Remove(id);
        }
        if (state.Run is { } run)
        {
            foreach (uint id in run.Alive.Keys.Where(id => !MonsterShown(player, id)).ToList()) run.Alive.Remove(id);
            run.Daemons.Clear();
            await Create(player, run.Blocker, BlockerType, RitualWire.Blocker(state.Altars[run.Altar].Cell, RitualTables.RadiusCells),
                $"0x2bb Ritual blocker 0x{run.Blocker:x}");
        }
    }

    // ---- the tick -----------------------------------------------------------------------------------------------------

    /// <summary>One tick (<see cref="GameEvents.AreaTick"/>), by the connection that steps the area's monsters: the altars a
    /// player came near show, deaths of tied monsters credit their altar, an altar whose tied monsters are all dead is ready,
    /// and a running ritual steps.</summary>
    public async Task Advance(Player runner, double seconds)
    {
        if (runner.Paused || Of(runner) is not { Done: false, Altars.Count: > 0 } state || !IsMonsterRunner(runner) || !Current(runner)) return;
        if (!await state.Gate.WaitAsync(0)) return;
        try
        {
            if (state.Done || !Current(runner)) return;
            await Sync(runner, state);
            state.Now += Math.Max(0, seconds);
            double now = AreaTime(runner);
            if (state.Now >= state.RevealAt && state.Altars.Any(a => !a.Shown))
            {
                state.RevealAt = state.Now + RevealSeconds;
                var here = Clients(runner).Select(c => c.Walk.Position(AreaTime(c))).DefaultIfEmpty(runner.Walk.Position(now)).ToList();
                for (int k = 0; k < state.Altars.Count; k++)
                    if (!state.Altars[k].Shown && here.Any(p => Distance(p, state.Altars[k].Cell) <= RitualTables.RevealDistance))
                        await Reveal(runner, state, k);
            }
            await Deaths(runner, state);
            for (int k = 0; k < state.Altars.Count; k++)
                if (state.Altars[k].State == Inactive && state.Altars[k].Shown) await CheckReady(runner, state, k);
            if (state.Run is { } run) await RunStep(runner, state, run);
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>The altar's three objects at its cell (the create order Interactable, Object, Light), then its guard packs,
    /// every guard tied to it.</summary>
    async Task Reveal(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        altar.Shown = true;
        if (!InMap(player.Layout, altar.Cell))
        {
            Console.WriteLine($"[ritual] altar at {altar.Cell} is outside {player.Layout.Id}'s grid; not sent");
            altar.State = Done;
            return;
        }
        altar.Interactable = player.TakeObjectId();
        altar.Object = player.TakeObjectId();
        altar.Light = player.TakeObjectId();
        await CreateAltar(player, altar);
        await SpawnGuards(player, state, k);
    }

    /// <summary>The altar's objects as its state has them now: the Interactable, the Object and, until it is done, the Light
    /// [capture: states 0/0, 1/1, 2/0 and 3/1 for the Interactable, the others the same value].</summary>
    static async Task CreateAltar(Player player, Altar altar)
    {
        float progress = Progress(altar);
        int enabled = altar.State is Ready or Done ? 1 : 0;
        await Create(player, altar.Interactable, InteractableType,
            RitualWire.Interactable(altar.Cell, altar.Rotation, altar.Rune.Row, progress, altar.State, enabled),
            $"0x2bb Ritual altar 0x{altar.Interactable:x} ({altar.Rune.Id}) at {altar.Cell}");
        await Create(player, altar.Object, ObjectType, RitualWire.Object(altar.Cell, altar.Rotation, altar.Rune.Row, progress, altar.State),
            $"0x2bb Ritual altar object 0x{altar.Object:x}");
        if (altar.Light != 0)
            await Create(player, altar.Light, LightType,
                RitualWire.Light(altar.Cell, altar.Rotation, altar.State, progress > 0 ? LightScale(progress) : null),
                $"0x2bb Ritual altar light 0x{altar.Light:x}");
    }

    /// <summary>One object of the area: its create, woken at once (a blob whose first byte has 0x02 wakes itself).</summary>
    static Task Create(Player player, uint id, string type, byte[] blob, string log) =>
        SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), blob), log,
            wake: (blob[0] & 0x02) == 0, awaitBirth: false);

    static Task CreatePool(Player player, Altar altar, uint pool, (int X, int Y) at) =>
        Create(player, pool, PoolType, RitualWire.Pool(at, 0, altar.Interactable), $"0x2bb Ritual BloodPool 0x{pool:x} for 0x{altar.Interactable:x}");

    async Task SpawnGuards(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        if (altar.Guards.Count == 0) return;
        var layout = player.Layout;
        var rng = new Random(HashCode.Combine(state.Seed, altar.Cell.X, altar.Cell.Y, k));
        foreach (var pack in altar.Guards)
        {
            (int X, int Y)? spot = null;
            for (int t = 0; t < 20 && spot is null; t++)
            {
                double a = rng.NextDouble() * Math.Tau;
                double r = RitualTables.GuardDistanceMin + rng.NextDouble() * (RitualTables.GuardDistanceMax - RitualTables.GuardDistanceMin);
                var c = ((int)Math.Round(altar.Cell.X + r * Math.Cos(a)), (int)Math.Round(altar.Cell.Y + r * Math.Sin(a)));
                if (Floor(layout, c)) spot = c;
            }
            if (spot is not { } at) continue;
            foreach (int row in pack)
            {
                var c = Snap(layout, (at.X + rng.Next(-RitualTables.GuardScatter, RitualTables.GuardScatter + 1),
                    at.Y + rng.Next(-RitualTables.GuardScatter, RitualTables.GuardScatter + 1))) ?? at;
                uint id = await Spawn(player, state, row, MonsterRarity.Normal, c, mine: false, rune: altar.Rune);
                if (id == 0) continue;
                state.Tie[id] = (k, Monsters.All[row].Id, MonsterRarity.Normal);
                state.Seen.Add(id);
                altar.Tied.Add(id);
            }
        }
    }

    /// <summary>Ties the monsters seen for the first time inside an inactive altar's circle (the nearest altar), and credits
    /// a tied monster's death to its altar: a BloodPool in the circle and the light's scale. Only while an altar is still
    /// inactive does it look at the area's monsters, twice a second; a hidden altar ties nothing (its monsters are the ones
    /// around it once a player has come near).</summary>
    async Task Deaths(Player player, State state)
    {
        var died = new List<(int Altar, string Path, MonsterRarity Rarity)>();
        int radius = RitualTables.RadiusCells;
        if (state.Now >= state.ScanAt && state.Altars.Any(a => a.State == Inactive && a.Shown))
        {
            state.ScanAt = state.Now + ScanSeconds;
            double now = AreaTime(player);
            foreach (var (id, m) in player.Area.Monsters)
            {
                if (state.Seen.Contains(id) || state.Mine.Contains(id) || m.Life == 0) continue;
                state.Seen.Add(id);
                // A friendly monster and a summon never join.
                if (!m.Hostile || m.OwnerId is not null || m.SummonerId is not null || m.Variety is null) continue;
                var cell = m.Walk.Position(now);
                int best = -1;
                double bestDistance = double.MaxValue;
                for (int k = 0; k < state.Altars.Count; k++)
                {
                    if (state.Altars[k].State != Inactive || !state.Altars[k].Shown) continue;
                    double d = Distance(cell, state.Altars[k].Cell);
                    if (d <= radius && d < bestDistance) (best, bestDistance) = (k, d);
                }
                if (best < 0) continue;
                state.Tie[id] = (best, m.Variety.Id, m.Rarity);
                state.Altars[best].Tied.Add(id);
            }
        }
        foreach (var (id, (k, path, rarity)) in state.Tie)
        {
            var altar = state.Altars[k];
            if (altar.Dead.Contains(id) || Alive(player, id)) continue;
            altar.Dead.Add(id);
            died.Add((k, path, rarity));
        }
        foreach (var (k, path, rarity) in died) await Credit(player, state, k, path, rarity);
    }

    async Task Credit(Player player, State state, int k, string path, MonsterRarity rarity)
    {
        var altar = state.Altars[k];
        if (path.Length > 0) altar.Credit.Add((path, rarity));
        if (!altar.Shown || altar.Interactable == 0) return;
        var rng = new Random(HashCode.Combine(state.Seed, altar.Cell.X, altar.Cell.Y, altar.Dead.Count));
        int radius = RitualTables.RadiusCells;
        for (int t = 0; t < 12; t++)
        {
            double a = rng.NextDouble() * Math.Tau, r = radius * Math.Sqrt(rng.NextDouble());
            var c = ((int)Math.Round(altar.Cell.X + r * Math.Cos(a)), (int)Math.Round(altar.Cell.Y + r * Math.Sin(a)));
            if (!Floor(player.Layout, c)) continue;
            uint pool = player.TakeObjectId();
            await Create(player, pool, PoolType, RitualWire.Pool(c, (float)(rng.NextDouble() * Math.Tau), altar.Interactable),
                $"0x2bb Ritual BloodPool 0x{pool:x} for 0x{altar.Interactable:x}");
            altar.Pools.Add(pool);
            break;
        }
        await SendWorldRecord(player, altar.Light, InstanceEntities.ComponentUpdate(altar.Light, RitualWire.LightPositioned,
            RitualWire.LightScale(LightScale(Progress(altar)))), $"0x2bc Ritual light 0x{altar.Light:x} scale", null);
    }

    /// <summary>Ready when every monster tied to the altar is dead (nothing tied: at once): 0x165 1, the Interactable's 07 01
    /// and StateMachine (1, 1), the Object's and the Light's StateMachine 1 [capture].</summary>
    async Task CheckReady(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        if (altar.Tied.Any(id => !altar.Dead.Contains(id))) return;
        altar.State = Ready;
        await Broadcast(player, RitualWire.AltarState(altar.Interactable, altar.Cell, Ready), $"0x165 Ritual altar 0x{altar.Interactable:x} ready");
        await States(player, altar, 1, [1, 1], 1);
    }

    static async Task States(Player player, Altar altar, byte action, int[] interactable, int state)
    {
        await SendWorldRecord(player, altar.Interactable, InstanceEntities.ComponentUpdate(altar.Interactable,
            RitualWire.InteractableAction, [action]), "0x2bc Ritual interactable 07", null);
        await SendWorldRecord(player, altar.Interactable, InstanceEntities.StateMachineUpdate(altar.Interactable,
            RitualWire.InteractableStates, new StateMachineData(interactable, 0)), "0x2bc Ritual interactable states", null);
        await SendWorldRecord(player, altar.Object, InstanceEntities.StateMachineUpdate(altar.Object,
            RitualWire.ObjectStates, new StateMachineData([state], 0)), "0x2bc Ritual object states", null);
        if (altar.Light != 0)
            await SendWorldRecord(player, altar.Light, InstanceEntities.StateMachineUpdate(altar.Light,
                RitualWire.LightStates, new StateMachineData([state], 0)), "0x2bc Ritual light states", null);
    }

    // ---- the click ------------------------------------------------------------------------------------------------------

    /// <summary>A click on an altar (<see cref="GameEvents.ObjectUse"/>): walk up to a ready one, start it only in reach with
    /// the action still current, one ritual at a time, and always end the action. Anything else is swallowed: the client
    /// opens the favours window from the last 0x166. False when the click was not this instance's altar.</summary>
    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Of(player) is not { } state || state.Altars.FirstOrDefault(a => a.Interactable != 0 && a.Interactable == use.TargetId) is not { } altar)
            return false;
        // A dead, leaving or just-arrived player's click is the altar's too, and does nothing.
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return true;
        if (altar.State != Ready || state.Run is not null)
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
            return true;
        }
        int size = QuestObjectTypes.Find(InteractableType)?.Size ?? 4;
        if (!TryBeginObjectWalk(player, requestedAt, altar.Cell.X, altar.Cell.Y, size, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y,
                altar.Cell.X, altar.Cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player starts Ritual altar 0x{altar.Interactable:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s",
                still: () => PlayerActionCurrent(player, action));
        try
        {
            if (arrive > now) await AreaDelay(player, arrive - now);
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
            if (!PlayerWalk.InReach(stop, altar.Cell, size, player.Layout.Walkable)) return true;
            await state.Gate.WaitAsync();
            try
            {
                int k = state.Altars.IndexOf(altar);
                if (k >= 0 && altar.State == Ready && state.Run is null && !state.Done) await Start(player, state, k);
            }
            finally { state.Gate.Release(); }
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>The start: the revived are the altar's credited monsters and, once more, every one an earlier altar of the
    /// area revived [one pool shared by the altars]; their places the rune .ao's locators in the RitualSpawnPatterns order.
    /// 0x165 2, 0xdf on, the Interactable's 07 00 and StateMachine (2, 0), the Object's and the Light's 2, the
    /// RitualBlocker [capture: the 0xdf follows the 0x165 by 6 records].</summary>
    async Task Start(Player player, State state, int k)
    {
        var altar = state.Altars[k];
        var rng = new Random(HashCode.Combine(state.Seed, altar.Cell.X, altar.Cell.Y, state.Completed));
        var monsters = altar.Credit.Select(c => new Revived { Path = c.Path, Rarity = c.Rarity, N = 1 })
            .Concat(state.Revived.Select(m => new Revived { Path = m.Path, Rarity = m.Rarity, N = m.N + 1 })).ToList();
        Shuffle(monsters, rng);
        var locators = RitualTables.LocatorsOf(altar.Rune.Ao);
        var order = new List<(double X, double Y)>();
        foreach (string part in altar.Rune.Order)
        {
            var points = locators.Part(part).ToList();
            Shuffle(points, rng);
            order.AddRange(points);
        }
        if (order.Count == 0) order = locators.Outside.Count > 0 ? [.. locators.Outside] : [(0, 0)];
        altar.State = Running;
        var run = new Run { Altar = k, StartedAt = state.Now, Monsters = monsters, Order = order, Rng = rng, Blocker = player.TakeObjectId() };
        state.Run = run;
        state.Revived = [];
        await Broadcast(player, RitualWire.AltarState(altar.Interactable, altar.Cell, Running), $"0x165 Ritual altar 0x{altar.Interactable:x} starts");
        if (altar.Rune.Environment is int env)
            await Broadcast(player, InstanceEnvironments.Data(((ushort)env, 0)), $"0xdf Ritual environment {env} on");
        await States(player, altar, 0, [2, 0], 2);
        await Create(player, run.Blocker, BlockerType, RitualWire.Blocker(altar.Cell, RitualTables.RadiusCells), $"0x2bb Ritual blocker 0x{run.Blocker:x}");
    }

    /// <summary>The running ritual: the daemon every EveryMs, the waves, the tribute of each revived monster killed (0x167),
    /// the faded pools' removal, and the end once every revived monster is spawned and dead.</summary>
    async Task RunStep(Player player, State state, Run run)
    {
        var altar = state.Altars[run.Altar];
        var layout = player.Layout;
        double t = state.Now - run.StartedAt;
        if (altar.Rune.Daemon is { Varieties.Count: > 0, EveryMs: > 0 } daemon)
            while (run.DaemonsDue < Math.Max(1, daemon.Max) && t >= run.DaemonsDue * daemon.EveryMs / 1000.0)
            {
                var spots = RitualTables.LocatorsOf(altar.Rune.Ao).Daemon;
                var xy = spots.Count > 0 ? spots[run.Rng.Next(spots.Count)] : (0, 0);
                run.DaemonsDue++;
                if (Snap(layout, RitualTables.LocatorCell(altar.Cell, altar.Rotation, xy)) is not { } c) continue;
                int row = daemon.Varieties[0];
                // A daemon variety whose object type is a real monster (its .ot extends a monster base) spawns as one, at
                // the run's level, and counts like the run's other monsters: the run waits for it, its death adds tribute
                // and it comes back at the next altar. A variety with no monster base stays a daemon object.
                if (row >= 0 && row < Monsters.All.Count && Monsters.All[row] is { Supported: true } variety
                    && RitualTables.IsMonsterType(variety)
                    && await Spawn(player, state, row, MonsterRarity.Normal, c, mine: true, rune: altar.Rune) is uint monster and not 0)
                    run.Alive[monster] = new Revived { Path = variety.Id, Rarity = MonsterRarity.Normal, N = 1 };
                else if (await CreateDaemon(player, state, row, c) is uint id and not 0)
                    run.Daemons.Add(id);
            }
        double first = RitualTables.FirstWaveFrames * RitualTables.Frame, spacing = RitualTables.WaveSpacingFrames * RitualTables.Frame,
            gap = RitualTables.WaveGapFrames * RitualTables.Frame;
        if (run.Waves.Count == 0 && t >= first) run.Waves.Add(new Wave { At = t });
        if (run.Waves.Count > 0)
        {
            var wave = run.Waves[^1];
            while (wave.Spawned < RitualTables.WaveSize && run.Next < run.Monsters.Count && t >= wave.At + wave.Spawned * spacing)
            {
                var m = run.Monsters[run.Next];
                run.Next++;
                wave.Spawned++;
                (int X, int Y)? cell = null;
                for (int i = 0; i < run.Order.Count && cell is null; i++)
                    cell = Snap(layout, RitualTables.LocatorCell(altar.Cell, altar.Rotation, run.Order[(run.Next + i) % run.Order.Count]));
                if (cell is not { } at || Monsters.Find(m.Path) is not { Supported: true } variety
                    || !variety.Id.Equals(m.Path, StringComparison.Ordinal)) continue;
                uint id = await Spawn(player, state, variety.Row, m.Rarity, at, mine: true, rune: altar.Rune);
                if (id == 0) continue;
                wave.Ids.Add(id);
                run.Alive[id] = m;
                if (altar.Pools.Count > 0)
                {
                    uint pool = altar.Pools[0];
                    altar.Pools.RemoveAt(0);
                    await SendWorldRecord(player, pool, InstanceEntities.ComponentUpdate(pool, RitualWire.PoolTransition, [0x02]),
                        $"0x2bc Ritual BloodPool 0x{pool:x} fades", null);
                    altar.Faded.Add((pool, state.Now));
                }
            }
            if (wave.Spawned >= RitualTables.WaveSize && run.Next < run.Monsters.Count)
            {
                int alive = wave.Ids.Count(id => Alive(player, id));
                if (alive <= RitualTables.NextWaveAlive * Math.Max(1, wave.Ids.Count) || t >= wave.At + wave.Spawned * spacing + gap)
                    run.Waves.Add(new Wave { At = t });
            }
        }
        foreach (var (id, m) in run.Alive.Where(kv => !Alive(player, kv.Key)).ToList())
        {
            run.Alive.Remove(id);
            run.Killed++;
            state.Tribute += RitualTables.TributeOf(m.Rarity, m.N) * Math.Max(0, 100 + state.TributePct) / 100;
            await Broadcast(player, RitualWire.Tribute(state.Tribute), $"0x167 Ritual tribute {state.Tribute}");
            state.Revived.Add(m);
        }
        foreach (var faded in altar.Faded.Where(f => state.Now - f.At >= RitualTables.PoolFadeFrames * RitualTables.Frame).ToList())
        {
            await SendWorldRemoval(player, faded.Pool, $"0x2bd Ritual BloodPool 0x{faded.Pool:x}");
            altar.Faded.Remove(faded);
        }
        if (run.Next >= run.Monsters.Count && run.Alive.Count == 0 && run.Waves.Count > 0 && t >= first)
            await Finish(player, state, run);
    }

    /// <summary>The end: 0x165 3, 0xdf off, 0x166 with the new tribute and done, 0x167 [capture: both runs of the level 77 area
    /// send them in this order], the Interactable's 07 01 and StateMachine (3, 1), the Object's and the Light's 3, then the
    /// daemons, the blocker, the light and the pools go.</summary>
    async Task Finish(Player player, State state, Run run)
    {
        var altar = state.Altars[run.Altar];
        altar.State = Done;
        state.Completed++;
        await Broadcast(player, RitualWire.AltarState(altar.Interactable, altar.Cell, Done), $"0x165 Ritual altar 0x{altar.Interactable:x} done");
        if (altar.Rune.Environment is int env)
            await Broadcast(player, InstanceEnvironments.Data(((ushort)env, RitualTables.EndEnvironmentSeconds)), $"0xdf Ritual environment {env} off");
        await Broadcast(player, RitualWire.Favours(state.Tribute, RitualTables.FavoursRows(state.Completed), state.Altars.Count, state.Completed),
            $"0x166 Ritual favours ({state.Completed}/{state.Altars.Count} done)");
        await Broadcast(player, RitualWire.Tribute(state.Tribute), $"0x167 Ritual tribute {state.Tribute}");
        await States(player, altar, 1, [3, 1], 3);
        foreach (uint daemon in run.Daemons) await SendWorldRemoval(player, daemon, $"0x2bd Ritual daemon 0x{daemon:x}");
        await SendWorldRemoval(player, run.Blocker, $"0x2bd Ritual blocker 0x{run.Blocker:x}");
        await SendWorldRemoval(player, altar.Light, $"0x2bd Ritual light 0x{altar.Light:x}");
        foreach (uint pool in altar.Pools.Concat(altar.Faded.Select(f => f.Pool)))
            await SendWorldRemoval(player, pool, $"0x2bd Ritual BloodPool 0x{pool:x}");
        altar.Pools.Clear();
        altar.Faded.Clear();
        altar.Light = 0;
        state.Run = null;
        if (state.Altars.All(a => a.State >= Done)) state.Done = true;
    }

    // ---- monsters -----------------------------------------------------------------------------------------------------

    /// <summary>A ritual monster: the rune's buff rides on it, its BuffDefinitions stats as the spawn's extra stats and its
    /// ModsKey rows as extra mods. A rune with neither spawns as any monster does.</summary>
    async Task<uint> Spawn(Player player, State state, int row, MonsterRarity rarity, (int X, int Y) cell, bool mine, RitualTables.Rune rune)
    {
        var all = Monsters.All;
        if (row < 0 || row >= all.Count || all[row] is not { Supported: true } variety)
        {
            Console.WriteLine($"[ritual] MonsterVarieties row {row} has no supported variety; not spawned");
            return 0;
        }
        if (!InMap(player.Layout, cell)) return 0;
        uint id = await SpawnMonster(player, variety, (byte)Math.Clamp(state.Level, 1, 100), rarity, cell.X, cell.Y, log: false,
            extraStats: rune.BuffStats is { Count: > 0 } stats ? stats : null, extraMods: RitualTables.RuneModSet(rune, state.Level));
        if (id != 0 && mine) state.Mine.Add(id);
        return id;
    }

    /// <summary>A daemon as the client sees it: its 0x20 type (every client of the area is told once) and its create; not in
    /// the fight. 0 when the catalogue cannot write it.</summary>
    static async Task<uint> CreateDaemon(Player player, State state, int row, (int X, int Y) cell)
    {
        var all = Monsters.All;
        if (row < 0 || row >= all.Count || all[row] is not { Supported: true } variety || !InMap(player.Layout, cell)) return 0;
        byte level = (byte)Math.Clamp(state.Level, 1, 100);
        if (player.Area.MonsterTypes.Add((variety.Row, level)))
        {
            var types = InstanceEntities.MonsterTypes([((ushort)variety.Row, level)]);
            string log = $"0x0020 {variety.Id}@{level}";
            if (player.Area.Owned) player.Area.Delta.Add(0, types, log, AreaDelta.Kind.Message);
            if (!player.Connection.WorldOutput.Started) await Send(player, types, log);
        }
        uint id = player.TakeObjectId();
        state.Mine.Add(id);
        var blob = new MonsterObject(cell.X, cell.Y, 1, variety.ComponentsOrBase, StateMachine: variety.StateMachine).Blob();
        await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MonsterObject.TypePath(variety.Id, level)), blob),
            $"0x2bb Ritual daemon 0x{id:x} {variety.Id.Split('/')[^1]}", wake: true, awaitBirth: false);
        return id;
    }

    /// <summary>A monster counts as living while it is awake and alive or waits dormant to emerge.</summary>
    static bool Alive(Player player, uint id) =>
        player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0 || player.Area.DormantMonsters.ContainsKey(id);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    static float Progress(Altar altar) => altar.Tied.Count == 0 ? 0 : (float)altar.Dead.Count / altar.Tied.Count;

    static float LightScale(float progress) => (float)(1 + RitualTables.LightGain * Math.Clamp(progress, 0, 1));

    static bool InMap(AreaLayout layout, (int X, int Y) c) => c.X >= 0 && c.Y >= 0 && c.X < layout.GridW && c.Y < layout.GridH;

    static bool Floor(AreaLayout layout, (int X, int Y) c) => InMap(layout, c) && layout.WalkableFor(c.X, c.Y, 0);

    /// <summary>The cell itself when it is floor, else the nearest floor cell within 6; null if none.</summary>
    static (int X, int Y)? Snap(AreaLayout layout, (int X, int Y) c)
    {
        if (Floor(layout, c)) return c;
        (int D, (int X, int Y) At)? best = null;
        for (int dx = -6; dx <= 6; dx++)
            for (int dy = -6; dy <= 6; dy++)
            {
                var q = (c.X + dx, c.Y + dy);
                int d = dx * dx + dy * dy;
                if (Floor(layout, q) && (best is null || d < best.Value.D)) best = (d, q);
            }
        return best?.At;
    }

    static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
