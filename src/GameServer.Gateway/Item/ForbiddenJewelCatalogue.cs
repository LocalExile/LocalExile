using GameServer.Core.Protocol.Instance;

/// <summary>Native Forbidden jewel variants. Pairing grants effects, never a paid allocation or a class change.</summary>
public static class ForbiddenJewelCatalogue
{
    public const string Flame = "Forbidden Flame", Flesh = "Forbidden Flesh";
    const string FlameMod = "PuzzlePieceCleansingFireUnique__1", FleshMod = "PuzzlePieceGreatTangleUnique__1";
    public sealed record Notable(ushort Id, string Name, string Ascendancy);
    static readonly Lazy<IReadOnlyList<Notable>> Entries = new(Load);
    public static IReadOnlyList<Notable> All => Entries.Value;

    static IReadOnlyList<Notable> Load()
    {
        var tree = new PassiveTree();
        var result = new AscendancyCatalog().Definitions.Where(d => d.Identity.Family == AscendancyFamily.Normal)
            .SelectMany(d => d.Nodes.Values.Where(n => tree.Find(n.Id) is { IsNotable: true }
                && !n.IsStart && !n.IsChoice).Select(n => new Notable(n.Id, n.Name, d.Name))).ToList();
        // These seven Eldritch passives are intentionally not connected to a paid ascendancy tree.
        for (int i = 1; i <= 7; i++)
            if (tree.FindById($"AscendancySpecialEldritch{i}") is { } node && result.All(n => n.Id != node.GraphId))
                result.Add(new(node.GraphId, node.Name, "Eldritch"));
        if (tree.FindById("AscendancyTrickster14") is { Name: "Harness the Void" } harness
            && result.All(n => n.Id != harness.GraphId))
            result.Add(new(harness.GraphId, harness.Name, "Trickster"));
        return result.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id).ToArray();
    }

    public static Notable? Find(string query) => ushort.TryParse(query, out ushort id)
        ? All.FirstOrDefault(n => n.Id == id)
        : All.Where(n => n.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray() is [var only] ? only : null;

    public static Item? Create(ItemCatalog items, string kind, ushort node)
    {
        bool flame = kind.Equals("flame", StringComparison.OrdinalIgnoreCase) || kind.Equals(Flame, StringComparison.OrdinalIgnoreCase);
        if (!flame && !kind.Equals("flesh", StringComparison.OrdinalIgnoreCase) && !kind.Equals(Flesh, StringComparison.OrdinalIgnoreCase)) return null;
        if (All.All(n => n.Id != node) || items.Find(flame ? "Crimson Jewel" : "Cobalt Jewel") is not { } type
            || items.Mods.ById(flame ? FlameMod : FleshMod) is not { Stats.Count: 1 } mod) return null;
        string name = flame ? Flame : Flesh;
        if (UniqueJewelVisuals.Of(name) is not ushort visual || RadiusJewelCatalogue.WordHash(name, "data") is not uint word) return null;
        return ItemCatalog.Create(type) with
        {
            Rarity = ItemRarity.Unique, Corrupted = true, ItemLevel = 85,
            VisualIdentity = visual, NameWords = [word],
            Mods = [new(ModSection.Explicit, mod.Hash, [node])],
        };
    }

    public static (bool Flame, ushort Node)? Read(Item item, ModCatalog mods)
    {
        if (item.Rarity != ItemRarity.Unique || !item.Identified) return null;
        var matching = item.Mods.Where(m => m.Section == ModSection.Explicit
            && mods.ByHash(m.Hash)?.Id is FlameMod or FleshMod).ToArray();
        if (matching is not [var mod] || mod.Values is not [var value] || value is <= 0 or > ushort.MaxValue
            || All.All(n => n.Id != value)) return null;
        bool flame = mods.ByHash(mod.Hash)!.Id == FlameMod;
        if (item.TypeHash != InstanceEntities.ObjectTypeHash(flame ? "Metadata/Items/Jewels/JewelStr" : "Metadata/Items/Jewels/JewelInt")) return null;
        return (flame, (ushort)value);
    }

    public static ushort[] Granted(IEnumerable<Item> activeJewels, ModCatalog mods)
    {
        var flame = new HashSet<ushort>(); var flesh = new HashSet<ushort>();
        foreach (var item in activeJewels)
            if (Read(item, mods) is { } half) (half.Flame ? flame : flesh).Add(half.Node);
        flame.IntersectWith(flesh);
        return [.. flame.Order()];
    }
}
