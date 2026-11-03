using GameServer.Core.Protocol.Instance;


/// <summary>Removing one named modifier from an item, as the developer /item_mod remove does.</summary>
static class ItemModEdits
{
    /// <summary>The item without its first modifier with this hash in that section, or in any section when
    /// <paramref name="section"/> is null; the same item when it has none.</summary>
    public static Item Remove(Item item, ushort hash, ModSection? section = null)
    {
        int index = Enumerable.Range(0, item.Mods.Count)
            .FirstOrDefault(i => item.Mods[i].Hash == hash && (section is null || item.Mods[i].Section == section), -1);
        return index < 0 ? item : item with { Mods = [.. item.Mods.Where((_, i) => i != index)] };
    }

    /// <summary>Whether the item holds that modifier.</summary>
    public static bool Contains(Item item, ushort hash, ModSection? section = null) =>
        item.Mods.Any(m => m.Hash == hash && (section is null || m.Section == section));
}
