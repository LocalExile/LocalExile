using System.Collections.Concurrent;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;

/// <summary>
/// The day's Labyrinth (roadmap world.md "The Labyrinth", the day's layout): its seed, the layout the client builds from
/// it (<see cref="LabyrinthLayout"/>), the area each room is played in, and the labyrinth node an area's extra data names
/// (<c>0x141ec7220</c> flag 0x08; with it the client applies the room's LabyrinthNodeOverrides and secrets, and the
/// server's level generator must too, <see cref="ServerLevels"/>).
/// The official server's daily seed and its pick of a room's area are not in the client: here the seed is the UTC day's
/// number since 1970 (<c>Game_LAB_SEED</c> overrides it) and a room's area is one of its LabyrinthAreas row's areas for
/// the difficulty, chosen from the seed and the room's place in the layout.
/// </summary>
static class LabyrinthDay
{
    static readonly ConcurrentDictionary<(uint Seed, int Difficulty), Day> Days = new();

    /// <summary>A built day: its layout and each node's WorldAreas Id.</summary>
    public sealed record Day(LabyrinthLayout Layout, IReadOnlyList<string?> Areas);

    /// <summary>Today's seed.</summary>
    public static uint Seed =>
        uint.TryParse(Environment.GetEnvironmentVariable("Game_LAB_SEED"), out uint fixedSeed) ? fixedSeed
            : (uint)(DateTime.UtcNow.Date - DateTime.UnixEpoch).Days;

    /// <summary>The difficulty a Labyrinth area belongs to (1 Normal … 4 Eternal) by its id, or null.</summary>
    public static int? DifficultyOf(string areaId) =>
        areaId.StartsWith("1_Labyrinth", StringComparison.Ordinal) ? 1
        : areaId.StartsWith("2_Labyrinth", StringComparison.Ordinal) ? 2
        : areaId.StartsWith("3_Labyrinth", StringComparison.Ordinal) ? 3
        : areaId.StartsWith("EndGame_Labyrinth", StringComparison.Ordinal) ? 4 : null;

    /// <summary>The day's layout for a difficulty, with its rooms' areas.</summary>
    public static Day For(uint seed, int difficulty) => Days.GetOrAdd((seed, difficulty), key => Build(key.Seed, key.Difficulty));

    /// <summary>The labyrinth node of an instance: the room of a run played at this (area, seed) (<see cref="Enter"/>),
    /// else the first room of today's layout played in the area (a /goto), or null when the area is not in today's
    /// Labyrinth (it is then generated and sent without one, as before).</summary>
    public static LabyrinthNodeKey? NodeFor(string areaId, uint instanceSeed) =>
        RunRooms.TryGetValue((areaId, instanceSeed), out var room) ? room : NodeFor(areaId);

    /// <summary>The first room of today's layout played in the area, or null.</summary>
    public static LabyrinthNodeKey? NodeFor(string areaId)
    {
        if (DifficultyOf(areaId) is not int difficulty) return null;
        uint seed = Seed;
        Day day;
        try { day = For(seed, difficulty); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            Console.WriteLine($"[gateway] labyrinth day {seed}: {ex.Message}");
            return null;
        }
        int index = day.Areas.ToList().IndexOf(areaId);
        return index < 0 ? null : new LabyrinthNodeKey(seed, day.Layout.Nodes[index].Row, difficulty);
    }

    // A character's run (ours, kept while the server runs): the day and tier it is of, each room's instance and seed, the
    // rooms been in (the map's progress) and the room now. It starts at the plaza's way in and ends when the character is
    // anywhere but one of its rooms (a portal or a death's way to town, the plaza, another area).
    sealed class Run(uint seed, int difficulty)
    {
        public readonly uint Seed = seed;
        public readonly int Difficulty = difficulty;
        public readonly Dictionary<int, (ulong Instance, uint Seed)> Rooms = [];
        public readonly HashSet<int> Visited = [];
        public int Node = -1;
        public long Number = Interlocked.Increment(ref runs);
    }
    static long runs;
    static readonly ConcurrentDictionary<string, Run> Runs = new(StringComparer.Ordinal);
    // Which room of a run an instance is (its area and the seed the run gave it): its generation and 0x1d's node.
    static readonly ConcurrentDictionary<(string Area, uint Seed), LabyrinthNodeKey> RunRooms = new();

    static int IndexOf(Day day, int row) => day.Layout.Nodes.ToList().FindIndex(n => n.Row == row);

    /// <summary>The way to a room of a run from a transition: the plaza's way in starts a new run of today's Normal
    /// Labyrinth at its first room; an exit of a run's room goes to the neighbour in the layout played in the exit's area
    /// (its instance when the run has been there, else a new one at a seed the run picks, so the server knows its room).
    /// Null: not a run's way (an ordinary transfer).</summary>
    public static PendingTransfer? Transfer(string character, string fromArea, uint fromSeed, string? sourceType, ushort destination, string? destinationArea)
    {
        Run? run;
        int to;
        if (fromArea == Plaza && sourceType == Entrance)
        {
            run = Runs[character] = new Run(Seed, 1);
            to = 0;
            Console.WriteLine($"[gateway] {character}: Labyrinth run {run.Number} of day {run.Seed} starts");
        }
        else
        {
            if (!Runs.TryGetValue(character, out run) || NodeFor(fromArea, fromSeed) is not { } from || from.Seed != run.Seed) return null;
            var day = For(run.Seed, run.Difficulty);
            int index = IndexOf(day, from.Row);
            if (index < 0) return null;
            to = day.Layout.Nodes[index].Links.Where(l => l >= 0).FirstOrDefault(l => day.Areas[l] == destinationArea, -1);
            if (to < 0) return null;
        }
        lock (run)
        {
            if (run.Rooms.TryGetValue(to, out var known) && known.Instance != 0)
                return new PendingTransfer(destination, known.Instance, false);
            var day = For(run.Seed, run.Difficulty);
            uint seed = (uint)(HashCode.Combine(character, run.Number, to) & 0x7fffffff) | 2;
            run.Rooms[to] = (0, seed);
            RunRooms[(day.Areas[to]!, seed)] = new LabyrinthNodeKey(run.Seed, day.Layout.Nodes[to].Row, run.Difficulty);
            return new PendingTransfer(destination, 0, true)
            {
                Seed = seed,
                Done = answer =>
                {
                    if (!answer.Ok) return;
                    lock (run) run.Rooms[to] = (answer.Instance, seed);
                },
            };
        }
    }

    /// <summary>A character enters an area: in a room of their run it is remembered (the map's progress to send,
    /// <see cref="Progress"/>); anywhere else their run ends. The progress, or null outside a run.</summary>
    public static byte[]? Enter(string character, string areaId, uint instanceSeed)
    {
        if (!Runs.TryGetValue(character, out var run)) return null;
        var node = RunRooms.TryGetValue((areaId, instanceSeed), out var room) && room.Seed == run.Seed ? room : null;
        var day = For(run.Seed, run.Difficulty);
        int index = node is null ? -1 : IndexOf(day, node.Row);
        lock (run)
            if (index < 0 || !run.Rooms.TryGetValue(index, out var mine) || mine.Seed != instanceSeed)
            {
                Runs.TryRemove(new KeyValuePair<string, Run>(character, run));
                Console.WriteLine($"[gateway] {character}: Labyrinth run {run.Number} ends in {areaId}");
                return null;
            }
            else
            {
                run.Node = index;
                run.Visited.Add(index);
                return Progress(run.Visited);
            }
    }

    /// <summary>The map's progress outside a run (a /goto into a room of today's Labyrinth): that room alone.</summary>
    public static byte[] Visit(LabyrinthNodeKey node) => Progress([IndexOf(For(node.Seed, node.Difficulty), node.Row)]);

    /// <summary>S→C 0x1cb, the run's progress (dispatcher case 0x84, 96 bits, bit i in byte i / 8): the Labyrinth map
    /// (LabyrinthMapWindow, <c>0x140bcdf20</c>) shows a room whose bit 0x11 + its index is set, and Izaro's rooms (a
    /// section's last, +0x5a = slot + 1) with his icon until bit slot + 1 is set. The secrets' numbers
    /// (<see cref="LabyrinthLayout.Node.Number1"/>) start after the rooms' bits.</summary>
    public static byte[] Progress(IEnumerable<int> rooms)
    {
        var bytes = new byte[2 + 12];
        bytes[0] = 0x01; bytes[1] = 0xcb;
        foreach (int room in rooms)
            if (room + 0x11 is int bit and < 96) bytes[2 + bit / 8] |= (byte)(1 << (bit % 8));
        return bytes;
    }

    /// <summary>The Aspirants' Plaza: where the first room's way out (its -1 connection) leads.</summary>
    public const string Plaza = "Labyrinth_Airlock";

    /// <summary>The plaza's way into the Labyrinth (the entrance tile's LabyrinthAreaTransition) and the instance state of
    /// its door, which the Labyrinth chosen at the device (C→S 0x212) opens: the way in is shown only then.</summary>
    public const string Entrance = "Metadata/Terrain/Labyrinth/Objects/LabyrinthAreaTransition";
    public static readonly string PlazaDoorOpen = Quests.InstanceFlag("PlazaDoor");

    /// <summary>An area's exits today, as its WorldAreas connections would be: the room's "entranceN" leads to its N-th
    /// connection in the layout (the client counts a passage secret's "entrance&lt;N&gt;" after them the same way), that
    /// room's area; the first room's way out to the plaza. The last room's way out (to Izaro) has none yet. Null when the
    /// area is not in today's Labyrinth.</summary>
    public static IReadOnlyList<(int Index, string Area)>? Exits(string areaId, uint instanceSeed)
    {
        if (NodeFor(areaId, instanceSeed) is not { } node) return null;
        var day = For(node.Seed, node.Difficulty);
        int index = day.Layout.Nodes.ToList().FindIndex(n => n.Row == node.Row);
        var exits = new List<(int, string)>();
        var links = day.Layout.Nodes[index].Links;
        for (int n = 0; n < links.Count; n++)
        {
            string? to = links[n] >= 0 ? day.Areas[links[n]] : index == 0 ? Plaza : null;
            if (to is not null) exits.Add((n + 1, to));
        }
        return exits;
    }

    /// <summary>The area extra data (0x1d's terrain block) naming the node: flags 0x08, 0x00, then u32 seed, u16 node
    /// (its LabyrinthSectionLayout row), u8 difficulty, little-endian (<c>0x141ec7220</c>).</summary>
    public static byte[] ExtraData(LabyrinthNodeKey node)
    {
        var bytes = new byte[9];
        bytes[0] = 0x08;
        BitConverter.TryWriteBytes(bytes.AsSpan(2), node.Seed);
        BitConverter.TryWriteBytes(bytes.AsSpan(6), (ushort)node.Row);
        bytes[8] = (byte)node.Difficulty;
        return bytes;
    }

    /// <summary>The day's layout as text lines for /lab_layout: each room's number, area, section and place, connections
    /// (out: the plaza or the boss) and secrets with their numbers.</summary>
    public static IEnumerable<string> Describe(uint seed, int difficulty)
    {
        static byte[]? Read(string path) => GamePack.Default.TryRead(path);
        var day = For(seed, difficulty);
        var sections = new RawDat(Read("Data/LabyrinthSection.datc64")!);
        var secrets = new RawDat(Read("Data/LabyrinthSecrets.datc64")!);
        string Secret(int row, int number) => row < 0 ? "" : $" {secrets.String(row, 0)}#{number}";
        for (int i = 0; i < day.Layout.Nodes.Count; i++)
        {
            var n = day.Layout.Nodes[i];
            yield return $"{i}: {day.Areas[i] ?? "(no area)"} — {sections.String(n.Section, 0)} {n.Place}, to " +
                string.Join(", ", n.Links.Select(l => l < 0 ? "out" : l.ToString())) +
                Secret(n.Secret1, n.Number1) + Secret(n.Secret2, n.Number2);
        }
    }

    static Day Build(uint seed, int difficulty)
    {
        static byte[]? Read(string path) => GamePack.Default.TryRead(path);
        var layout = LabyrinthLayout.Build(Read, seed, difficulty);
        var layouts = new RawDat(Read("Data/LabyrinthSectionLayout.datc64") ?? throw new FileNotFoundException("LabyrinthSectionLayout"));
        var areas = new RawDat(Read("Data/LabyrinthAreas.datc64") ?? throw new FileNotFoundException("LabyrinthAreas"));
        var worldAreas = new RawDat(Read("Data/WorldAreas.datc64") ?? throw new FileNotFoundException("WorldAreas"));
        // LabyrinthAreas: Id +0, then the Normal, Cruel, Merciless and Endgame WorldAreas lists (+8, +0x18, +0x28, +0x38).
        int column = 8 + 0x10 * (difficulty - 1);
        var picked = new List<string?>();
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            int row = layouts.Ref(layout.Nodes[i].Row, 0x44);
            int[] choices = row < 0 ? [] : areas.RefList(row, column);
            picked.Add(choices.Length == 0 ? null
                : worldAreas.String(choices[(int)(unchecked(seed * 0x9e3779b1u + (uint)i * 0x85ebca77u) % (uint)choices.Length)], 0));
        }
        return new Day(layout, picked);
    }
}
