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

    /// <summary>The labyrinth node of an area today: the first room of the day's layout played in it, or null when the
    /// area is not in today's Labyrinth (it is then generated and sent without one, as before).</summary>
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

    // The rooms each character has been in today, per layout (ours, kept while the server runs).
    static readonly ConcurrentDictionary<(string Character, uint Seed, int Difficulty), HashSet<int>> Visited = new();

    /// <summary>A character enters a room of today's Labyrinth: it is remembered, and the run's progress to send
    /// (<see cref="Progress"/>) has every room they have been in.</summary>
    public static byte[] Visit(string character, LabyrinthNodeKey node)
    {
        var day = For(node.Seed, node.Difficulty);
        int index = day.Layout.Nodes.ToList().FindIndex(n => n.Row == node.Row);
        var rooms = Visited.GetOrAdd((character, node.Seed, node.Difficulty), _ => []);
        lock (rooms)
        {
            if (index >= 0) rooms.Add(index);
            return Progress(rooms);
        }
    }

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

    /// <summary>An area's exits today, as its WorldAreas connections would be: the room's "entranceN" leads to its N-th
    /// connection in the layout (the client counts a passage secret's "entrance&lt;N&gt;" after them the same way), that
    /// room's area; the first room's way out to the plaza. The last room's way out (to Izaro) has none yet. Null when the
    /// area is not in today's Labyrinth.</summary>
    public static IReadOnlyList<(int Index, string Area)>? Exits(string areaId)
    {
        if (NodeFor(areaId) is not { } node) return null;
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
