using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The waypoint areas in the order of the client's waypoint bitset (S→C 0x4c, Agents/docs/protocol/16-waypoints.md):
/// the WorldAreas rows with <c>HasWaypoint</c> whose column at row +0x1e4 (<c>__col51</c> in the WorldAreas table:
/// 1 town, 2 area, 11 the Labyrinth's Aspirants' Plaza) is 1, 2, 5 or 11, in table order (0x141fe37b0 counts them the
/// same way, up to 150). 3.29.3b and 3.29.3c have 90, from Lioneye's Watch (bit 0) to Aspirants' Plaza (bit 89).
/// <para>AccessFlags: the QuestFlags rows of the row's list at +0x54 (<c>EndQuestFlags</c> in the extracted table, a name
/// that does not fit): the World panel shows the area's pin only when the player has all of them (0x14077ecf0), e.g.
/// a1q1-KilledHillock for Lioneye's Watch and The Coast, a1q4-PlacedGlyphs for The Submerged Passage.</para>
/// </summary>
public sealed class WorldAreaWaypoints
{
    public sealed record Entry(string Id, string Name, ushort Hash16, bool IsTown, IReadOnlyList<int>? AccessFlags = null);

    /// <summary>Row +0x1e4 values that count (0x826 = bits 1, 2, 5, 11).</summary>
    static readonly int[] Kinds = [1, 2, 5, 11];

    public const int MaxWaypoints = 150;

    public IReadOnlyList<Entry> Entries { get; }

    public WorldAreaWaypoints(IReadOnlyList<Entry> entries) => Entries = entries;

    /// <summary>The bit of an area's waypoint, or null when the area has none.</summary>
    public int? BitOf(ushort hash16)
    {
        for (int i = 0; i < Entries.Count; i++) if (Entries[i].Hash16 == hash16) return i;
        return null;
    }

    public Entry? Find(ushort hash16) => BitOf(hash16) is int i ? Entries[i] : null;

    public Entry? FindById(string id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>Loads the order from the WorldAreas table; empty without it or when a column is missing.</summary>
    public static WorldAreaWaypoints Load(ClientTable? worldAreas)
    {
        if (worldAreas is null) return new([]);
        try
        {
            var entries = new List<Entry>();
            foreach (var r in worldAreas)
            {
                if (!r.Bool("HasWaypoint")) continue;
                if (r.IntOrNull("__col51") is not { } kind || !Kinds.Contains(kind)) continue;
                entries.Add(new(r.Text("Id")!, r.Text("Name") ?? "", (ushort)r.Int("HASH16"), r.Bool("IsTown"), r.Ints("EndQuestFlags")));
                if (entries.Count == MaxWaypoints) break;
            }
            return new(entries);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or InvalidCastException)
        {
            return new([]);
        }
    }
}
