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
