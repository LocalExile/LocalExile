using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // fx_item_local: a stat that belongs to its item (Stats.dat IsLocal), which the character's sum never sees, counts as
    // a character stat while the item is worn in the row's slot: "Left ring slot: +250 to maximum Energy Shield"
    // (Shavronne's Revelation), a ring's own "#% more ..." lines. Read while the effect rules are on.
    sealed record ItemLocalTable(object Rows, (uint Input, byte Slot, uint Output, double Scale)[] Entries, HashSet<uint> Inputs);
    static ItemLocalTable _itemLocalTable = new(new object(), [], []);
    const byte AnySlot = 0;

    static ItemLocalTable ItemLocalFromTable()
    {
        var rows = GameData.FxItemLocal;
        var cached = Volatile.Read(ref _itemLocalTable);
        if (ReferenceEquals(cached.Rows, rows)) return cached;
        var entries = new List<(uint, byte, uint, double)>();
        int unknown = 0;
        foreach (var row in rows)
        {
            byte? slot = row.Slot switch { "any" => AnySlot, "left_ring" => LeftRing, "right_ring" => RightRing, _ => null };
            if (slot is byte at && Program.Stats.Key(row.Stat) is uint input && Program.Stats.Key(row.CountsAs) is uint output)
                entries.Add((input, at, output, row.Scale));
            else unknown++;
        }
        if (rows.Count > 0)
            Console.WriteLine($"[rules] {entries.Count} item-local line(s) from the fx tables" + (unknown > 0 ? $"; {unknown} left out (a stat or slot this build does not have)" : ""));
        Volatile.Write(ref _itemLocalTable, cached = new(rows, [.. entries], [.. entries.Select(e => e.Item1)]));
        return cached;
    }

    /// <summary>Adds the table's rows for the worn, active items to a fresh source sum. One pass over the worn items;
    /// an item's local stats are read from the per-item cache.</summary>
    public static void ApplyItemLocalRows(IReadOnlyList<(byte Row, InventoryItem Item)> equipment, ISet<uint> inactive,
        ModCatalog mods, Dictionary<uint, int> stats)
    {
        if (!StatConditions.UseTable) return;
        var table = ItemLocalFromTable();
        if (table.Entries.Length == 0) return;
        for (int e = 0; e < equipment.Count; e++)
        {
            var (row, worn) = equipment[e];
            if (inactive.Contains(worn.Id)) continue;
            foreach (var (input, slot, output, scale) in table.Entries)
            {
                if (slot != AnySlot && slot != row) continue;
                int value = ItemModStats.LocalValue(worn.Item, mods, input);
                if (value != 0) GolemAdd(stats, output, ActorScale.Saturate((long)Math.Round(value * scale)));
            }
        }
    }
}
