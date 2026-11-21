using GameServer.Core.Protocol.Instance;

internal static class OffTreeAnointments
{
    internal readonly record struct Grant(Item Source, PassiveTree.Node Node);

    // Each enchantment carries one graph id. Adding two item stat values would give a different node.
    internal static List<Grant> Read(IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        ModCatalog mods, PassiveTree tree, IReadOnlySet<ushort> allocated)
    {
        List<Grant> grants = [];
        foreach (var (_, placed) in equipment)
            foreach (var mod in placed.Item.Mods)
            {
                if (mod.Section != ModSection.Enchant || mods.ByHash(mod.Hash)?.Id is not
                    ("GrantedPassive" or "GrantedPassive2" or "GrantedPassive3" or "GrantedPassive4" or "GrantedPassive5"))
                    continue;
                // An item can store the 16-bit graph id as either a signed or an unsigned stat value.
                if (mod.Values is [var id] && id is >= short.MinValue and <= ushort.MaxValue
                    && tree.Find(unchecked((ushort)id)) is { IsAnointmentOnly: true } node && !allocated.Contains(node.GraphId))
                    grants.Add(new(placed.Item, node));
            }
        return grants;
    }

    internal static IEnumerable<(uint Key, int Value)> Stats(List<Grant> grants, IReadOnlyList<Item> active)
    {
        HashSet<ushort> counted = [];
        foreach (var (source, node) in grants)
            if (active.Any(item => ReferenceEquals(source, item)) && counted.Add(node.GraphId))
                foreach (var stat in node.Stats) yield return stat;
    }
}
