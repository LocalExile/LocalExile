using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public const string BurningItemCondition = "_while_burning";
    public const string NoOtherLifeModifiersCondition = "_if_no_life_modifiers_on_equipment_except_body_armour";
    public static readonly IReadOnlySet<string> NamedItemConditionSuffixes =
        new HashSet<string>(StringComparer.Ordinal) { BurningItemCondition, NoOtherLifeModifiersCondition };

    // Life is the modifier's client tag, including implicit/enchant/hidden lists and zero-valued rolls. Socketed
    // gems, flasks and passive jewels are not ordinary equipped armour/accessories/weapons in this collection.
    public static IReadOnlyDictionary<string, bool> QualifiedEquipmentItemFlags(
        IReadOnlyDictionary<string, bool> captured, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        ItemCatalog catalog, IReadOnlyCollection<Item>? active = null)
    {
        bool lifeModifier = equipment.Any(e => e.Row != PlayerInventories.BodyArmour
            && (active is null || active.Any(item => ReferenceEquals(item, e.Item.Item)))
            && e.Item.Item.Mods.Any(m => catalog.Mods.ByHash(m.Hash)?.Tags.Contains("life") == true));
        return new Dictionary<string, bool>(captured) { [NoOtherLifeModifiersCondition] = !lifeModifier };
    }
}
