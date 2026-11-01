using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The Trials of Ascendancy (Agents/docs/game/labyrinth-trials.md): LabyrinthTrials.dat names each trial's area. Its
/// rooms (the area's <c>labyrinth_intro</c> / <c>labyrinth_end</c> nodes) hold the door, the levers and doors, the
/// plaque and the portal back as server object lines. The client counts a trial done when the character has the row's
/// quest flag (column 7, e.g. NormalLabyrinthCompletedPrison) or the league quest flag of column 1 (0x141f69230).
/// </summary>
static class LabyrinthTrials
{
    public sealed record Trial(string Area, string Flag, int LeagueFlag);

    public const string IntroDoor = "Metadata/Terrain/Labyrinth/Objects/LabyrinthIntroDoor",
        Door = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Door_Closed",
        Lever = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/Switch_Once",
        Plaque = "Metadata/QuestObjects/Labyrinth/LabyrinthTrialPlaque",
        Portal = "Metadata/Terrain/Labyrinth/Objects/LabyrinthTrialReturnPortal",
        SpikeTrap = "Metadata/Terrain/Labyrinth/Traps/LabyrinthCascadeSpikeTrap",
        ArrowTrap = "Metadata/Terrain/Labyrinth/Traps/LabyrinthArrowTrap",
        // The trials' Bronze Monographs (NPCs.dat LabyrinthTrialGlyphPrison, …Sins, …Church, …Crematorium, …Hedge,
        // …Catacombs), placed as NPCs.
        GlyphPrefix = "Metadata/NPC/Labyrinth/LabyrinthTrialGlyph";

    public const string Roomba = "Metadata/Terrain/Labyrinth/Traps/LabyrinthRoomba",
        FlyingRoomba = "Metadata/Terrain/Labyrinth/Traps/LabyrinthFlyingRoomba",
        Rail = "Metadata/Terrain/Labyrinth/Traps/Rail_Invisible";

    /// <summary>A roomba's speed in cells a second: its .ot's Movement <c>base_speed</c> (the player's Pathfinding
    /// base_speed 37 is 37 cells a second), and how long it waits at each rail (<c>pause_at_waypoint_duration</c> 100 ms).</summary>
    public static double RoombaSpeed(string type) => type == FlyingRoomba ? 25 : 35;
    public const double RoombaPauseSeconds = 0.1;

    /// <summary>How near a roomba's centre the player is in it (our number, from its look: about two tiles across; the
    /// model has no Hull, and it does not block: live 2026-10-31).</summary>
    public static double RoombaRadius = 10;

    /// <summary>What a roomba deals while the player is in it (live 2026-10-31, the original: damage all the time, no
    /// buff shown, no knockback): BuffDefinitions <c>labyrinth_trap_bleed</c> (invisible) takes
    /// <c>base_physical_damage_%_of_maximum_life_taken_per_minute</c> and <c>…_of_maximum_energy_shield_…</c>, whose values
    /// the client data lacks: these shares a second are ours, physical, energy shield first.
    /// <c>/trial_roomba_damage</c> (dev) changes them.</summary>
    public static (double Life, double EnergyShield) RoombaDamagePerSecond = (0.40, 0.25);

    /// <summary>A roomba's 0x2b9 blob: LabyrinthRoomba.ot reads Positioned, Animated, BaseEvents, Stats, Buffs, Movement,
    /// Transitionable; LabyrinthFlyingRoomba.ot Positioned, BaseEvents, Animated, Stats, Buffs, Movement. Movement
    /// (<c>0x141e741e0</c>): u8 (+0x30), i32 x, i32 y (the cell it sets Positioned to), four u32 (+0x3c, +0x40, +0x4c,
    /// +0x50), u8 +0x76 (non-zero starts its own movement, <c>0x141e74970</c>: not sent), u8 +0x77. The server moves it
    /// with 0x1ba.</summary>
    public static byte[] RoombaBlob(string type, int x, int y, float angle, string model)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).F32(angle).U16(14).U8(0).U8(0x02).U8(0).U8(1);    // Positioned: the .ot's team 14
        w.U8(1).WStr32(model);                                              // Animated (BaseEvents reads nothing)
        w.Stats(null).U32(0).U32(0).U8(0);                                  // Stats
        w.U16(0);                                                           // Buffs
        w.U8(0).I32(x).I32(y).U32(0).U32(0).U32(0).U32(0).U8(0).U8(0);      // Movement
        if (type == Roomba) w.U8(1);                                        // Transitionable: active
        return w.U8(0).ToArray();                                           // tasks
    }

    /// <summary>A roomba's way along the trial's invisible rails: its rails in order, and whether they close a ring.</summary>
    public sealed record RoombaPath(IReadOnlyList<(int X, int Y)> Rails, bool Ring);

    /// <summary>Each roomba's path (null when no rail runs under it), its rails in the order it goes. Rails join their
    /// nearest rail along each axis, within 6° (one of the Catacombs' runs is 5° off), when each is the other's nearest
    /// that way. A roomba stands on a run and faces along it, the way it sets out (its room line's angle, as a projectile
    /// object's rotation: direction (sin a, −cos a)): where two runs cross under it (the Catacombs' first ground roomba
    /// and the flying one's) the one along its facing is its own. Round a ring it keeps going that way (the Catacombs'
    /// square of four all face round it; live 2026-10-31, the original: clockwise on the screen); a chain it follows to
    /// its end and back (<c>path_following_method = "reverse_when_path_ends"</c>).</summary>
    public static RoombaPath?[] RoombaPaths(IReadOnlyList<(int X, int Y)> rails, IReadOnlyList<(int X, int Y, float Angle)> roombas)
    {
        const double MaxDeviation = 6 * Math.PI / 180, OnRail = 4;
        (int X, int Y)[] axes = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        int? Best(int i, (int X, int Y) d)
        {
            int? best = null;
            (double Deviation, double Distance) bestKey = (double.MaxValue, double.MaxValue);
            for (int j = 0; j < rails.Count; j++)
            {
                if (j == i) continue;
                double vx = rails[j].X - rails[i].X, vy = rails[j].Y - rails[i].Y;
                double along = vx * d.X + vy * d.Y, across = Math.Abs(vx * d.Y - vy * d.X);
                if (along <= 0) continue;
                double deviation = Math.Atan2(across, along);
                if (deviation > MaxDeviation) continue;
                var key = (deviation, along);
                if (key.CompareTo(bestKey) < 0) (best, bestKey) = (j, key);
            }
            return best;
        }
        var joined = Enumerable.Range(0, rails.Count).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < rails.Count; i++)
            foreach (var d in axes)
                if (Best(i, d) is int j && Best(j, (-d.X, -d.Y)) == i && !joined[i].Contains(j))
                {
                    joined[i].Add(j);
                    joined[j].Add(i);
                }
        static double ToSegment((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
        }
        var paths = new RoombaPath?[roombas.Count];
        for (int r = 0; r < roombas.Count; r++)
        {
            var (x, y, angle) = roombas[r];
            double fx = Math.Sin(angle), fy = -Math.Cos(angle);
            // How far a run's way (A to B) lies along the facing: 1 straight ahead, -1 straight behind.
            double Along(int a, int b)
            {
                double dx = rails[b].X - rails[a].X, dy = rails[b].Y - rails[a].Y, len = Math.Sqrt(dx * dx + dy * dy);
                return len == 0 ? 0 : (dx * fx + dy * fy) / len;
            }
            var under = Enumerable.Range(0, rails.Count).SelectMany(i => joined[i].Where(j => j > i).Select(j => (A: i, B: j)))
                .Select(e => (e.A, e.B, Distance: ToSegment((x, y), rails[e.A], rails[e.B]), Along: Along(e.A, e.B)))
                .Where(e => e.Distance <= OnRail)
                .OrderByDescending(e => Math.Abs(e.Along) >= Math.Cos(Math.PI / 6)).ThenBy(e => e.Distance).FirstOrDefault();
            if (under == default) continue;
            if (under.Along < 0) (under.A, under.B) = (under.B, under.A);
            // The run's rails in order: from one end of a chain, or round a ring.
            var seen = new HashSet<int> { under.A };
            var order = new List<int> { under.A };
            for (int at = under.A, next = under.B; next >= 0 && seen.Add(next); )
            {
                order.Add(next);
                (at, next) = (next, joined[next].FirstOrDefault(j => j != at && !seen.Contains(j), -1));
            }
            for (int at = under.B, back = under.A; ; )
            {
                int prev = joined[back].FirstOrDefault(j => j != at && !seen.Contains(j), -1);
                if (prev < 0) break;
                seen.Add(prev);
                order.Insert(0, prev);
                (at, back) = (back, prev);
            }
            bool ring = order.Count > 2 && joined[order[0]].Contains(order[^1]);
            paths[r] = new RoombaPath([.. order.Select(i => rails[i])], ring);
        }
        return paths;
    }

    /// <summary>A trial room's server spawners (points of its room file, with no .ot to tell what they spawn) and the
    /// monster each places, rare: the Lower Prison trial's PrisonPoacherSpawner, on the plaque's platform, a Brittle
    /// Poacher (live 2026-10-31, the original: a rare skeleton archer beside the platform's pack).</summary>
    public static readonly IReadOnlyDictionary<string, string> Spawners = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Metadata/QuestObjects/Labyrinth/PrisonPoacherSpawner"] = "Metadata/Monsters/Skeletons/SkeletonBowLightning",
    };

    /// <summary>How far from a trial's traps (the rails its roombas run on among them) the area's scattered packs keep
    /// their centres (our number: a pack's members stand within 9 cells of it, a spike tile reaches 11 from its trap).
    /// Live 2026-10-31, the original: no monsters among the traps.</summary>
    public const int TrapClearance = 25;

    /// <summary>A monograph's NPCTalk index (column 1): each has one row, 0 "Read" (NPCTalk 777–782).</summary>
    public const byte GlyphTalk = 0;

    /// <summary>A spike trap's square, where it hits and walls a player: its model's Hull (<c>spiketrap_prison.ao</c>,
    /// ±11.27) is one tile (23 cells), and the traps of a trial stand a tile apart, so the squares meet without a gap (live
    /// 2026-10-31: ±9 cells left four-cell strips between traps where nothing hit).</summary>
    public const int SpikeHalf = 11;

    /// <summary>How a spike trap fires: S→C 0x1d1 with Animation.dat <see cref="EmergeAnimation"/>, which its .act plays as
    /// <c>spike_fast</c> (<c>Spikes02/rig.amd</c>: 1000 ms; its sounds put the spikes up at 0 ms and start them down at
    /// 500 ms), stretched over this long. Every spike trap .act names that clip only. Live (2026-10-31): the Emerge action
    /// (0x1ab) played the plain clip, about a second late, with two traps of a 450 ms cascade up at once; stretched to
    /// 2000 ms, four stand up at once and lower slowly, as in the original. <c>/trial_spike_anim</c> (dev) changes it.
    /// </summary>
    public static double SpikeAnimationSeconds = 2;

    /// <summary>Animation.dat row <c>Emerge</c>.</summary>
    public const byte EmergeAnimation = 43;

    /// <summary>When after its firing (seconds) the spikes are up: a player in the square then is hit, once. Up from about the start of the stretched clip until it starts them down, at its half (live
    /// 2026-10-31: a hit only at 1200–1300 ms, fitted to the late action, missed a player who ran in while they were up).
    /// <c>/trial_spike_hit</c> (dev) moves it.</summary>
    public static (double From, double To) SpikeHitWindow = (0.1, 1.0);

    /// <summary>When after its firing (seconds) the square is impassable: from the spikes' rise until the stretched clip has
    /// lowered them (live 2026-10-31: the wall stands while any spike shows, lowering ones too).</summary>
    public static (double From, double To) SpikeWallWindow => (SpikeHitWindow.From, SpikeAnimationSeconds);

    /// <summary>How the spikes cycle (our own numbers: the real server's timers are the rooms' <c>Trigger_*</c> lines,
    /// server objects the client has no file for). A chain of spike traps fires as a wave, one trap
    /// <see cref="SpikeStepSeconds"/> after the other, so several stand up at once (live 2026-10-31, the original game: a
    /// cascade towards the door), and starts again as soon as it has gone round (live 2026-10-31: it
    /// does not stop). The cascade traps fire
    /// on their timer only; the spikes that answer a step (wiki) are another trap, in Izaro's rooms.</summary>
    /// <remarks><c>/trial_spike_step</c> (dev) changes it from the next wave on.</remarks>
    public static double SpikeStepSeconds = 0.45;

    /// <summary>A checkerboard chain's step: its two halves fire two seconds apart, every four (our reading of the room's
    /// <c>Trigger_Delay_4_0WG</c> / <c>Trigger_Delay_4_2WG</c> lines; live 2026-10-31: they rise together, as a
    /// checkerboard).</summary>
    public const double SpikeCheckerSeconds = 2;

    /// <summary>A chain's step: <see cref="SpikeCheckerSeconds"/> for a checkerboard, else <see cref="SpikeStepSeconds"/>.</summary>
    public static double SpikeStep(bool checker) => checker ? SpikeCheckerSeconds : SpikeStepSeconds;

    /// <summary>One chain's cycle: its wave of so many steps of so long.</summary>
    public static double SpikeWaveSeconds(int steps, double step) => steps * step;

    /// <summary>Each spike trap's chain and place in its wave: chains are traps a tile apart (8-neighbour, at most
    /// <see cref="SpikeHalf"/> × 2 + 2 cells). A chain's wave starts at its trap nearest an origin (the trial's levers;
    /// the real start is a <c>Trigger_PulsePrisonWG</c> line, which the room data lacks). A ring (every trap side by side
    /// with two others) goes once round, the way that reaches its trap nearest an end (the trial's doors) first (live
    /// 2026-10-31, checked against the original: from the lever straight towards the door, then on round), one <see cref="SpikeStepSeconds"/> step a trap. Any other chain
    /// fires as a checkerboard: the tiles of the start's colour, then the others, <see cref="SpikeCheckerSeconds"/>
    /// apart.</summary>
    public static (int Chain, int Order, bool Checker)[] SpikeChains(IReadOnlyList<(int X, int Y)> traps, IReadOnlyList<(int X, int Y)> origins,
        IReadOnlyList<(int X, int Y)> ends)
    {
        var placed = new (int Chain, int Order, bool Checker)[traps.Count];
        var done = new bool[traps.Count];
        int chain = 0;
        static bool Near((int X, int Y) a, (int X, int Y) b) => Math.Abs(a.X - b.X) <= SpikeHalf * 2 + 2 && Math.Abs(a.Y - b.Y) <= SpikeHalf * 2 + 2;
        static long Distance((int X, int Y) a, (int X, int Y) b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
        foreach (int first in Enumerable.Range(0, traps.Count).OrderBy(i => traps[i].Y).ThenBy(i => traps[i].X))
        {
            if (done[first]) continue;
            var members = new List<int> { first };
            done[first] = true;
            for (int k = 0; k < members.Count; k++)
                foreach (int i in Enumerable.Range(0, traps.Count).Where(i => !done[i] && Near(traps[members[k]], traps[i])))
                {
                    done[i] = true;
                    members.Add(i);
                }
            int start = origins.Count == 0 ? first
                : members.OrderBy(i => origins.Min(o => Distance(traps[i], o))).ThenBy(i => i).First();
            // Side by side: a corner is a step, not a diagonal shortcut.
            static bool Beside((int X, int Y) a, (int X, int Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) <= SpikeHalf * 2 + 2;
            var beside = members.ToDictionary(i => i, i => members.Where(j => j != i && Beside(traps[i], traps[j])).ToList());
            if (members.Count > 2 && beside.Values.All(b => b.Count == 2))
            {
                int end = ends.Count == 0 ? start
                    : members.OrderBy(i => ends.Min(o => Distance(traps[i], o))).ThenBy(i => i).First();
                List<int> Round(int next)
                {
                    var path = new List<int> { start };
                    for (int previous = start, at = next; at != start; (previous, at) = (at, beside[at].First(j => j != previous)))
                        path.Add(at);
                    return path;
                }
                var (one, other) = (Round(beside[start][0]), Round(beside[start][1]));
                var round = one.IndexOf(end) <= other.IndexOf(end) ? one : other;
                for (int k = 0; k < round.Count; k++) placed[round[k]] = (chain, k, false);
                chain++;
                continue;
            }
            // A checkerboard by tiles (SpikeHalf × 2 + 1 cells) from the start.
            int Tile(int d) => (int)Math.Round(d / (SpikeHalf * 2 + 1.0));
            foreach (int i in members)
                placed[i] = (chain, (Tile(traps[i].X - traps[start].X) + Tile(traps[i].Y - traps[start].Y)) & 1, true);
            chain++;
        }
        return placed;
    }

    /// <summary>A spike trap's hit only deals damage: live (2026-10-31, checked against the original) it neither stuns nor
    /// shows a buff; the risen spikes hold the player off as a wall (<see cref="SpikeHitWindow"/>). BuffDefinitions
    /// <c>pinned</c>, <c>trap_pinned</c> ("Spike Pinned", invisible, <c>base_physical_damage_taken_per_minute</c>) and
    /// <c>labyrinth_trap_bleed</c> exist; the last two carry damage over time whose values the client data lacks: not sent.
    /// </summary>
    /// <summary>A spike trap's hit always bleeds (its .ot: <c>main_hand_local_chance_to_bleed_on_hit_%</c> 100,
    /// <c>base_bleed_duration_+%</c> −80): the wiki's bleed is 70% of the hit's base physical damage a second, 10% when an
    /// enemy monster applied it (the trap is one: <c>is_daemon</c>), three times that while the target moves, for 5 s
    /// (1 s here), not reduced by armour; only the strongest bleed deals damage. Shown with BuffDefinitions
    /// <c>bleeding</c>; <c>labyrinth_trap_bleed</c> takes shares of the pools whose values the client data lacks.</summary>
    public const double SpikeBleedPerSecond = 0.10, SpikeBleedMovingFactor = 3, SpikeBleedSeconds = 5 * (1 - 0.8);

    /// <summary>The <c>bleeding</c> BuffDefinitions row, or null without the table.</summary>
    public static ushort? BleedingBuffRow => BuffRows.Value.TryGetValue("bleeding", out ushort row) ? row : null;

    /// <summary>The <c>poison</c> BuffDefinitions row ("Poisoned", a count of its stacks), or null without the table.</summary>
    public static ushort? PoisonBuffRow => BuffRows.Value.TryGetValue("poison", out ushort row) ? row : null;

    static readonly Lazy<Dictionary<string, ushort>> BuffRows = new(() =>
    {
        const string file = "data/tables/BuffDefinitions.json";
        var rows = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if (!File.Exists(file)) return rows;
        using var table = GameTables.Open(file);
        int row = 0;
        foreach (var buff in table.RootElement.EnumerateArray())
        {
            if (buff.GetProperty("Id").GetString() is { } id and ("bleeding" or "poison" or "labyrinth_arrow_slow")) rows.TryAdd(id, (ushort)row);
            row++;
        }
        return rows;
    });

    /// <summary>A spike trap's hit on a player (GrantedEffectStatSets <c>LbRetractableTrap</c>):
    /// <c>%_of_life_to_deal_as_damage</c> of maximum life plus <c>%_of_es_to_deal_as_damage</c> of maximum energy
    /// shield, with <c>damage_+%_vs_players</c>; it always hits and cannot be blocked. Null without the table. Armour and
    /// resistances are not applied (our reading: the trap deals a share of the pools).</summary>
    public static int? SpikeDamage(uint maxLife, uint maxEnergyShield) => TrapDamage(SpikeStats.Value, maxLife, maxEnergyShield);

    static readonly Lazy<(int Life, int EnergyShield, int VsPlayers)?> SpikeStats = new(() => TrapStats("LbRetractableTrap"));

    static int? TrapDamage((int Life, int EnergyShield, int VsPlayers)? stats, uint maxLife, uint maxEnergyShield) =>
        stats is { } s ? (int)Math.Min(int.MaxValue,
            ((long)maxLife * s.Life + (long)maxEnergyShield * s.EnergyShield) * (100 + s.VsPlayers) / 10000) : null;

    // A trap's stat set (GrantedEffectStatSets): its shares of the pools and its damage_+%_vs_players.
    static (int Life, int EnergyShield, int VsPlayers)? TrapStats(string id)
    {
        const string file = "data/tables/GrantedEffectStatSets.json";
        try
        {
            if (!File.Exists(file) || !File.Exists("data/tables/Stats.json")) return null;
            using var stats = JsonDocument.Parse(File.ReadAllBytes("data/tables/Stats.json"));
            var names = stats.RootElement.EnumerateArray().Select(s => s.GetProperty("Id").GetString() ?? "").ToList();
            using var sets = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var set in sets.RootElement.EnumerateArray())
            {
                if (set.GetProperty("Id").GetString() != id) continue;
                var values = set.GetProperty("ConstantStats").EnumerateArray().Select(k => names[k.GetInt32()])
                    .Zip(set.GetProperty("ConstantStatsValues").EnumerateArray().Select(v => v.GetInt32()))
                    .ToDictionary(p => p.First, p => p.Second);
                return (values.GetValueOrDefault("%_of_life_to_deal_as_damage"), values.GetValueOrDefault("%_of_es_to_deal_as_damage"),
                    values.GetValueOrDefault("damage_+%_vs_players"));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            Console.WriteLine($"[gateway] {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A spike trap's 0x2b9 blob (LabyrinthCascadeSpikeTrap.ot: Positioned, Animated, BaseEvents, Pathfinding,
    /// TriggerableBlockage, Transitionable, Stats, Buffs, Life, Actor), as a monster writes these components: the .ot's
    /// team 14, its own model, unblocked, state 1, one life, and no skills (it fires with the Emerge basic action, whose
    /// clip is its <c>spike_fast</c>).</summary>
    public static byte[] SpikeTrapBlob(int x, int y)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).U32(0).U16(14).U8(0).U8(0x02).U8(0).U8(1);     // Positioned
        w.U8(0);                                                        // Animated: the .ot's
        w.U8(0);                                                        // TriggerableBlockage: unblocked
        w.U8(1);                                                        // Transitionable
        w.Stats(null).U32(0).U32(0).U8(0);                              // Stats
        w.U16(0);                                                       // Buffs
        foreach (uint pool in new uint[] { 1, 0, 0 }) w.U32(pool).U32(0).U16(0).U32(0).U32(0);
        w.U8(0);                                                        // Life flags
        w.WStr32("").U16(0).U8(0).U32(0).U32(0).U8(0).U8(0);            // Actor
        return w.U8(0).ToArray();                                       // tasks
    }

    /// <summary>How often the dart traps fire (live 2026-10-31, the original's Imperial Gardens: every turret at once,
    /// about 1.5 s apart; the real timer is the server's).</summary>
    public const double ArrowVolleySeconds = 1.5;

    /// <summary>A dart trap's shot (Projectiles.dat): the poison darts, for its skill's <c>global_poison_on_hit</c>.</summary>
    public const string ArrowProjectile = "Metadata/Projectiles/PoisonArrowTrapProjectile";

    /// <summary>A dart trap's four shots: its .ot's <c>number_of_additional_projectiles</c> 3 and <c>projectiles_nova</c>,
    /// so a quarter turn apart from the way the turret faces (its room line's angle, as a projectile object's rotation:
    /// direction (sin a, −cos a)). Each leaves <c>projectile_nova_extend_projectiles_forward_by_x_units</c> (50) ahead
    /// of the turret, clear of its statue.</summary>
    public static IEnumerable<(double X, double Y)> ArrowDirections(float angle) =>
        Enumerable.Range(0, 4).Select(k => angle + k * Math.PI / 2).Select(a => (Math.Sin(a), -Math.Cos(a)));

    public static double ArrowForwardCells => 50 / WorldUnits.PerCell;

    /// <summary>A turret's Hull (<c>statueturret_circleclean.ao</c>, as every turret model but <c>_noblock</c>): squares
    /// of ±3.25 cells for walking and ±2.26 for projectiles, turned with the turret. The client stamps it, so a dart stops
    /// at another turret and a player walks round one (live 2026-10-31: the darts vanished at the turrets while the
    /// server's flew on).</summary>
    public const double ArrowTrapWalkHalf = 3.25, ArrowTrapProjectileHalf = 2.26;

    /// <summary>The cells whose centres lie in an object's Hull rectangle of these half sizes (cells, its model's
    /// walk_tri / proj_tri), turned by its angle.</summary>
    public static IEnumerable<(int X, int Y)> HullCells(int x, int y, float angle, double halfX, double halfY)
    {
        int reach = (int)Math.Ceiling(Math.Sqrt(halfX * halfX + halfY * halfY));
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
                if (Math.Abs(dx * cos + dy * sin) <= halfX && Math.Abs(-dx * sin + dy * cos) <= halfY)
                    yield return (x + dx, y + dy);
    }

    /// <summary>A trial door's walk Hull, half sizes in cells (across the way, through it), by its room line's model:
    /// <c>LabyrinthDoor.ao</c> (the trial's door) ±14 × ±6, <c>preview_labyrinthdoornofx.ao</c> (the Catacombs' Locked
    /// Door) ±14 × ±3.6, <c>doorbar_prison.ao</c> ±2 × ±5.5.</summary>
    public static (double X, double Y) DoorHull(string model) => Path.GetFileName(model).ToLowerInvariant() switch
    {
        "preview_labyrinthdoornofx.ao" => (14, 3.6),
        "doorbar_prison.ao" => (2, 5.5),
        _ => (14, 6),
    };

    /// <summary>Where the darts leave the turret: its rig's <c>cast</c> contact (<c>StatueTurret/rig.amd</c>, 118.757 up).</summary>
    public static int ArrowLaunchHeight => new ContactPoint(0, 0, -118.757f).HeightSteps;

    /// <summary>Animation.dat <c>SpellProjectile</c>, which the turret's .act plays as its <c>cast</c> clip (1000 ms, the
    /// contact at 0 ms).</summary>
    public const byte CastAnimation = 20;

    /// <summary>A dart's hit on a player (GrantedEffectStatSets <c>LbArrowTrap</c>: 15% of maximum life plus 9% of maximum
    /// energy shield, 33% more against players), read as the spike trap's (<see cref="SpikeDamage"/>).</summary>
    public static int? ArrowDamage(uint maxLife, uint maxEnergyShield) => TrapDamage(ArrowStats.Value, maxLife, maxEnergyShield);

    static readonly Lazy<(int Life, int EnergyShield, int VsPlayers)?> ArrowStats = new(() => TrapStats("LbArrowTrap"));

    /// <summary>What a dart's poison is reckoned from: its hit's share of maximum life alone. The poison goes past energy
    /// shield, so the whole hit's would turn an energy shield character's own pool against their life (live 2026-10-31:
    /// about 100 000 energy shield took a 13 135 hit, whose poison, 3 940 over 2 s, killed 3 707 life; our reading).</summary>
    public static int? ArrowPoisonBase(uint maxLife) => ArrowDamage(maxLife, 0);

    /// <summary>A dart's hit poisons (<c>global_poison_on_hit</c>) for 2 s; every hit adds a stack of its own. Chaos
    /// damage: past energy shield, less the player's chaos resistance. Its base is the hit's life share only
    /// (<see cref="ArrowPoisonBase"/>), and a stack deals this much of it a second, fitted by eye: the wiki's 30% with
    /// the .ot's <c>monster_poison_damage_+%_final</c> −50 made 111 a second for a 3 707 life character, too much
    /// against the original, which looked about 70 (live 2026-10-31). <c>/trial_dart_poison</c> (dev) changes it.</summary>
    public static double ArrowPoisonPerSecond = 0.10;
    public const double ArrowPoisonSeconds = 2;

    /// <summary>A dart's slow: BuffDefinitions <c>labyrinth_arrow_slow</c> (invisible), whose stat is this, for the skill's
    /// <c>base_skill_effect_duration</c> (2 s) from the latest hit. Its value is not in the client data: live
    /// (2026-10-31, the original) a character's movement speed went from +19% to −52% under the darts,
    /// (1 − 0.52) / 1.19 ≈ 0.40 of it, so 60% less, final.</summary>
    public const string ArrowSlowStat = "labyrinth_arrow_movement_speed_+%_final";
    public const int ArrowSlowPercent = -60;
    public const double ArrowSlowSeconds = 2;

    /// <summary>The <c>labyrinth_arrow_slow</c> BuffDefinitions row, or null without the table.</summary>
    public static ushort? ArrowSlowBuffRow => BuffRows.Value.TryGetValue("labyrinth_arrow_slow", out ushort row) ? row : null;

    /// <summary>A dart trap's 0x2b9 blob (LabyrinthArrowTrap.ot: Positioned, Transitionable, Animated, Pathfinding,
    /// BaseEvents, Stats, Buffs, Life, Actor), as the spike trap's (<see cref="SpikeTrapBlob"/>): the .ot's team 14, the
    /// room line's angle and model, state 1, one life, no skills (the server fires its darts).</summary>
    public static byte[] ArrowTrapBlob(int x, int y, float angle, string model)
    {
        var w = new BlobWriter().U8(0).U8(0);
        w.I32(x).I32(y).F32(angle).U16(14).U8(0).U8(0x02).U8(0).U8(1); // Positioned
        w.U8(1);                                                        // Transitionable
        w.U8(1).WStr32(model);                                          // Animated: the room line's
        w.Stats(null).U32(0).U32(0).U8(0);                              // Stats
        w.U16(0);                                                       // Buffs
        foreach (uint pool in new uint[] { 1, 0, 0 }) w.U32(pool).U32(0).U16(0).U32(0).U32(0);
        w.U8(0);                                                        // Life flags
        w.WStr32("").U16(0).U8(0).U32(0).U32(0).U8(0).U8(0);            // Actor
        return w.U8(0).ToArray();                                       // tasks
    }

    /// <summary>The components each type's 0x2b9 blob reads, in its order (scripts/re/ot_components.py; BaseEvents reads
    /// nothing but counts for the 0x2ba component index).</summary>
    static readonly Dictionary<string, string[]> Components = new(StringComparer.Ordinal)
    {
        [IntroDoor] = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "Transitionable", "TriggerableBlockage"],
        [Door] = ["Positioned", "InteractionAction", "BaseEvents", "Animated", "Transitionable", "TriggerableBlockage", "MinimapIcon"],
        [Lever] = ["Positioned", "Transitionable", "InteractionAction", "BaseEvents", "Animated", "MinimapIcon"],
        [Plaque] = ["Positioned", "BaseEvents", "Animated", "Transitionable", "MinimapIcon", "InteractionAction"],
        [Portal] = ["Positioned", "InteractionAction", "BaseEvents", "Animated", "Transitionable", "MinimapIcon"],
    };

    /// <summary>Transitionable states: 1 runs create_untransitioned (closed, unpulled, dark), 2 the transition (num_states 2).</summary>
    public const byte Closed = 1, Open = 2;

    /// <summary>Positioned sizes (object_size − 1): the doors and the lever 4, the plaque and the portal 6.</summary>
    public static int Size(string type) => type is Plaque or Portal ? 5 : 3;

    public static bool Handles(string type) => Components.ContainsKey(type);

    /// <summary>The Transitionable component's index in the type's component order.</summary>
    public static byte TransitionableIndex(string type) => (byte)Array.IndexOf(Components[type], "Transitionable");

    /// <summary>The trial in this area, or null.</summary>
    public static Trial? For(string areaId) => All.Value.GetValueOrDefault(areaId);

    static readonly Lazy<Dictionary<string, Trial>> All = new(() =>
    {
        try
        {
            if (!File.Exists("data/tables/LabyrinthTrials.json") || !File.Exists("data/tables/WorldAreas.json")) return [];
            using var areas = JsonDocument.Parse(File.ReadAllBytes("data/tables/WorldAreas.json"));
            var areaIds = areas.RootElement.EnumerateArray().Select(a => a.GetProperty("Id").GetString() ?? "").ToList();
            var flags = Quests.ParseRows(File.Exists("data/tables/QuestFlags.json") ? File.ReadAllText("data/tables/QuestFlags.json") : "[]")
                .ToDictionary(kv => kv.Value, kv => kv.Key);
            return Parse(File.ReadAllText("data/tables/LabyrinthTrials.json"), areaIds, flags).ToDictionary(t => t.Area);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] data/tables/LabyrinthTrials.json: {ex.Message}");
            return [];
        }
    });

    /// <summary>LabyrinthTrials rows: WorldAreas key, column 1 the league quest flag id, column 7 the QuestFlags row.</summary>
    public static List<Trial> Parse(string json, IReadOnlyList<string> areaIds, IReadOnlyDictionary<int, string> flagIds)
    {
        using var doc = JsonDocument.Parse(json);
        var trials = new List<Trial>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            int area = row.GetProperty("WorldAreas").GetInt32(), flag = row.GetProperty("__col7").GetInt32();
            if (area < 0 || area >= areaIds.Count || !flagIds.TryGetValue(flag, out var name)) continue;
            trials.Add(new Trial(areaIds[area], name, row.GetProperty("__col1").GetInt32()));
        }
        return trials;
    }

    /// <summary>A trial object's 0x2b9 blob: its type's components (<see cref="Components"/>) with the room line's model,
    /// angle and scale. TriggerableBlockage (0x141e44290) is blocked with no sections while closed.</summary>
    public static byte[] Blob(string type, string model, int x, int y, float angle, float scale, byte state)
    {
        var w = new BlobWriter().U8(0).U8(0);
        foreach (string c in Components[type])
            switch (c)
            {
                // x, y, rotation, u16, flags0 (0x20: uniform scale, then the x / y stretches), flags1 0x02 (shown).
                case "Positioned":
                    bool scaled = scale != 1;
                    w.I32(x).I32(y).F32(angle).U16(0).U8((byte)(scaled ? 0x20 : 0)).U8(0x02);
                    if (scaled) w.F32(scale).F32(1).F32(1);
                    w.U8(0).U8(1);
                    break;
                case "Animated": if (model.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)) w.U8(1).WStr32(model); else w.U8(0); break;
                case "InteractionAction": w.U8(0); break;
                case "Transitionable": w.U8(state); break;
                case "TriggerableBlockage": if (state == Closed) w.U8(1).U8(0); else w.U8(0); break;
                case "MinimapIcon": w.U8(0); break;
            }
        return w.U8(0).ToArray();   // tasks
    }
}
