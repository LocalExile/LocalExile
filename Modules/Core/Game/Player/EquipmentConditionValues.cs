using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public static Dictionary<string, int> EquipmentConditionValues(
        IReadOnlyList<(byte Row, InventoryItem Item)> equipment, ItemCatalog catalog,
        IReadOnlyCollection<Item>? active = null)
    {
        var qualified = equipment.Where(e => active is null
            || active.Any(item => ReferenceEquals(item, e.Item.Item))).ToArray();
        var result = new Dictionary<string, int>();
        foreach (var influence in Enum.GetValues<ItemInfluence>().Where(i => i != ItemInfluence.None))
            result["equipped_" + influence.ToString().ToLowerInvariant()] = qualified.Count(e => e.Item.Item.Influences.HasFlag(influence));
        foreach (var rarity in new[] { ItemRarity.Normal, ItemRarity.Magic, ItemRarity.Rare, ItemRarity.Unique })
            result["equipped_" + rarity.ToString().ToLowerInvariant()] = qualified.Count(e => e.Item.Item.Rarity == rarity);
        result["magic_ring_equipped"] = qualified.Count(e => e.Row is 6 or 7 && e.Item.Item.Rarity == ItemRarity.Magic);
        result["equipped_corrupted"] = qualified.Count(e => e.Item.Item.Corrupted);
        result["equipped_uncorrupted"] = qualified.Length - result["equipped_corrupted"];
        result["equipped_one_handed_sword"] = qualified.Count(e => catalog.ByHash(e.Item.Item.TypeHash)?.Class
            is "One Hand Sword" or "Thrusting One Hand Sword");
        result["equipped_unique_influence_amoung_equipped_non_amulet"] = Enum.GetValues<ItemInfluence>()
            .Count(i => i != ItemInfluence.None && qualified.Any(e => catalog.ByHash(e.Item.Item.TypeHash)?.Class != "Amulet"
                && e.Item.Item.Influences.HasFlag(i)));
        return result;
    }
}
