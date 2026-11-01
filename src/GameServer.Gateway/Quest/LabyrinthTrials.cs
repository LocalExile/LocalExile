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
        // The trials' Bronze Monographs (NPCs.dat LabyrinthTrialGlyphPrison, …Sins, …Church, …Crematorium, …Hedge,
        // …Catacombs), placed as NPCs.
        GlyphPrefix = "Metadata/NPC/Labyrinth/LabyrinthTrialGlyph";

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
    public static ushort? BleedingBuffRow => BleedingRow.Value;

    static readonly Lazy<ushort?> BleedingRow = new(() =>
    {
        const string file = "data/tables/BuffDefinitions.json";
        if (!File.Exists(file)) return null;
        using var table = GameTables.Open(file);
        int row = 0;
        foreach (var buff in table.RootElement.EnumerateArray())
        {
            if (buff.GetProperty("Id").GetString() == "bleeding") return (ushort)row;
            row++;
        }
        return null;
    });

    /// <summary>A spike trap's hit on a player (GrantedEffectStatSets <c>LbRetractableTrap</c>):
    /// <c>%_of_life_to_deal_as_damage</c> of maximum life plus <c>%_of_es_to_deal_as_damage</c> of maximum energy
    /// shield, with <c>damage_+%_vs_players</c>; it always hits and cannot be blocked. Null without the table. Armour and
    /// resistances are not applied (our reading: the trap deals a share of the pools).</summary>
    public static int? SpikeDamage(uint maxLife, uint maxEnergyShield) =>
        SpikeStats.Value is { } s ? (int)Math.Min(int.MaxValue,
            ((long)maxLife * s.Life + (long)maxEnergyShield * s.EnergyShield) * (100 + s.VsPlayers) / 10000) : null;

    static readonly Lazy<(int Life, int EnergyShield, int VsPlayers)?> SpikeStats = new(() =>
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
                if (set.GetProperty("Id").GetString() != "LbRetractableTrap") continue;
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
    });

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
