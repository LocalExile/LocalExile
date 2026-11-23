using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public const string BurningItemCondition = "_while_burning";
    public const string NoOtherLifeModifiersCondition = "_if_no_life_modifiers_on_equipment_except_body_armour";
    const string AllPiecesHaveArmour = "_if_all_equipment_grants_armour";
    public static readonly IReadOnlySet<string> NamedItemConditionSuffixes =
        new HashSet<string>(StringComparer.Ordinal) { BurningItemCondition, NoOtherLifeModifiersCondition, AllPiecesHaveArmour };

    // Item records are immutable. Rechecking the condition does not need to recalculate their tooltip defences.
    sealed record ArmourPresence(ItemCatalog Catalog, bool HasArmour);
    static readonly RuleTable<Item, ArmourPresence> ItemArmour = new();

    static bool GrantsArmour(Item item, ItemCatalog catalog)
    {
        if (ItemArmour.TryGetValue(item, out var known) && ReferenceEquals(known.Catalog, catalog))
            return known.HasArmour;
        bool hasArmour = catalog.ByHash(item.TypeHash) is { } type && catalog.Defences(type) is { } defences
            && ItemDefences.Calculate(item, defences, catalog.Mods).Armour > 0;
        ItemArmour.AddOrUpdate(item, new(catalog, hasArmour));
        return hasArmour;
    }

    // Life is the modifier's client tag, including implicit/enchant/hidden lists and zero-valued rolls. Socketed
    // gems, flasks and passive jewels are not ordinary equipped armour/accessories/weapons in this collection.
    public static IReadOnlyDictionary<string, bool> QualifiedEquipmentItemFlags(
        IReadOnlyDictionary<string, bool> captured, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        ItemCatalog catalog, IReadOnlyCollection<Item>? active = null)
    {
        bool Active(Item item) => active is null || active.Any(worn => ReferenceEquals(worn, item));
        bool lifeModifier = equipment.Any(e => e.Row != PlayerInventories.BodyArmour && Active(e.Item.Item)
            && e.Item.Item.Mods.Any(m => catalog.Mods.ByHash(m.Hash)?.Tags.Contains("life") == true));
        const int armourSlots = (1 << PlayerInventories.Helmet) | (1 << PlayerInventories.BodyArmour)
            | (1 << PlayerInventories.Gloves) | (1 << PlayerInventories.Boots);
        int armouredSlots = 0;
        foreach (var (row, worn) in equipment)
            if (row is PlayerInventories.Helmet or PlayerInventories.BodyArmour or PlayerInventories.Gloves or PlayerInventories.Boots
                && Active(worn.Item) && GrantsArmour(worn.Item, catalog))
                armouredSlots |= 1 << row;
        return WithFlags(captured, (NoOtherLifeModifiersCondition, !lifeModifier), (AllPiecesHaveArmour, armouredSlots == armourSlots));
    }
}
