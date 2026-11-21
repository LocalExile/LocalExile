using GameServer.Core.Protocol.Instance;

internal static class VaalVesselIncursionItems
{
    sealed record Reward(string ItemClass, string ModId);

    static readonly RuleTable<GameData.Snapshot, Reward[]> Rewards = new();
    static readonly RuleTable<ItemCatalog, ItemCatalog.BaseType[]> Equipment = new();

    internal static IEnumerable<ItemDrops.Drop> Roll(ItemCatalog items, ItemRolls rolls, int level, int count, Random random)
    {
        var rewards = Rewards.GetValue(GameData.Current, _ => [.. GameData.Store!
            .LoadRows("incursion_item_mods", "item_class, mod_id")
            .Select(row => new Reward((string)row["item_class"]!, (string)row["mod_id"]!))]);
        var choices = rewards.Select(r => (r.ItemClass, Mod: items.Mods.ById(r.ModId)
                ?? throw new InvalidOperationException($"Unknown Incursion reward modifier {r.ModId}")))
            .Where(r => level >= r.Mod.Level && level <= r.Mod.MaxLevel)
            .GroupBy(r => r.ItemClass)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Mod).ToArray());
        var equipment = Equipment.GetValue(items, catalog =>
            [.. catalog.All.Where(t => ItemDrops.EquipmentClasses.Contains(t.Class) && ItemDrops.Droppable(t))]);
        var bases = equipment.Where(t => t.DropLevel <= level && choices.ContainsKey(t.Class))
            .GroupBy(t => t.Class).ToDictionary(g => g.Key, g => g.ToArray());

        for (int n = 0; n < count; n++)
        {
            var itemClass = ItemDrops.EquipmentClass(random, bases.ContainsKey)
                ?? throw new InvalidOperationException($"No Incursion reward equipment at item level {level}");
            var eligible = bases[itemClass];
            var type = eligible[random.Next(eligible.Length)];
            var mods = choices[itemClass];
            var (sockets, links) = items.DropSockets(type, level, random);
            var item = ItemCatalog.Create(type, sockets, links, random) with { ItemLevel = (byte)level };
            var rare = rolls.Roll(item, ItemRarity.Rare, random, guaranteed: mods[random.Next(mods.Length)]);
            yield return new(rare with { Corrupted = true, Identified = true }, type);
        }
    }
}
