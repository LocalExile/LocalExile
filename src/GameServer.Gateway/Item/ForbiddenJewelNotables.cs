using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using System.Collections.Frozen;

/// <summary>The graph ids carried by the two native Forbidden jewel modifiers.</summary>
public static class ForbiddenJewelNotables
{
    public const string FlameMod = "PuzzlePieceCleansingFireUnique__1", FleshMod = "PuzzlePieceGreatTangleUnique__1";
    static readonly Lazy<FrozenDictionary<ushort, byte>> ClassByNotable = new(Load);
    static readonly Lazy<ushort[]> RollPool = new(() => ClassByNotable.Value.Keys.Order().ToArray());

    static FrozenDictionary<ushort, byte> Load()
    {
        var ordinary = new AscendancyCatalog().Definitions.Where(d => d.Identity.Family == AscendancyFamily.Normal
            && d.Name != "Reliquarian").ToArray();
        var classes = ordinary.ToDictionary(d => d.TableRow, d => d.BaseClass);
        var rows = ClientTables.Table("PassiveSkills").Where(r => r.Int("PassiveSkillGraphId") is > 0 and <= ushort.MaxValue)
            .ToDictionary(r => (ushort)r.Int("PassiveSkillGraphId"));
        var result = new Dictionary<ushort, byte>();
        foreach (var d in ordinary)
            foreach (var n in d.Nodes.Values)
                if (!n.IsStart && !n.IsChoice && n.PointsGranted == 0
                    && (d.Name == "Ascendant" ? n.IsChoiceOption : rows[n.Id].Bool("IsNotable") && !n.IsChoiceOption))
                    result[n.Id] = d.BaseClass;

        // Exclusive Forbidden nodes are not connected to the paid ascendancy graph. The obsolete
        // Eldritch Soul Drinker row was superseded by Harness the Void in 3.19.
        foreach (var (id, row) in rows)
        {
            string passive = row.Text("Id") ?? "";
            if ((passive.StartsWith("AscendancySpecialEldritch", StringComparison.Ordinal) && passive != "AscendancySpecialEldritch6"
                    || passive == "AscendancyTrickster14" && row.Text("Name") == "Harness the Void")
                && row.IntOrNull("AscendancyKey") is int key && classes.TryGetValue(key, out byte family))
                result[id] = family;
        }
        return result.ToFrozenDictionary();
    }

    public static ushort? Roll(Random random) =>
        RollPool.Value is { Length: > 0 } pool ? pool[random.Next(pool.Length)] : null;

    public static ushort? Granted(IEnumerable<InventoryItem> equipped, ItemCatalog items, byte baseClass, int level)
    {
        ushort flame = 0, flesh = 0;
        foreach (var entry in equipped)
        {
            var item = entry.Item;
            if (item is not { Rarity: ItemRarity.Unique, Identified: true, Corrupted: true }) continue;
            foreach (var mod in item.Mods)
            {
                if (mod.Section != ModSection.Explicit || mod.Values is not [> 1 and <= ushort.MaxValue and var value]) continue;
                string? id = items.Mods.ByHash(mod.Hash)?.Id;
                if (id is not (FlameMod or FleshMod) ||
                    items.ByHash(item.TypeHash) is not { } type ||
                    ItemModStats.LevelRequirement(item, type.DropLevel, items.Mods) > level) continue;
                if (!ClassByNotable.Value.TryGetValue((ushort)value, out byte family) || family != baseClass) continue;
                switch (id, type.Id)
                {
                    case (FlameMod, "Metadata/Items/Jewels/JewelStr"): flame = (ushort)value; break;
                    case (FleshMod, "Metadata/Items/Jewels/JewelInt"): flesh = (ushort)value; break;
                }
            }
        }
        return flame > 0 && flame == flesh ? flame : null;
    }
}
