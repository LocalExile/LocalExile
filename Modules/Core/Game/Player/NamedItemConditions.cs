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
        var copy = new Dictionary<string, bool>(captured);
        ApplyQualifiedEquipmentItemFlags(copy, equipment, catalog, active);
        return copy;
    }

    internal static void ApplyQualifiedEquipmentItemFlags(
        Dictionary<string, bool> flags, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        ItemCatalog catalog, IReadOnlyCollection<Item>? active = null)
    {
        bool lifeModifier = false;
        for (int i = 0; i < equipment.Count && !lifeModifier; i++)
        {
            var e = equipment[i];
            if (e.Row == PlayerInventories.BodyArmour) continue;
            if (active is not null)
            {
                bool found = false;
                foreach (var item in active)
                    if (ReferenceEquals(item, e.Item.Item)) { found = true; break; }
                if (!found) continue;
            }
            var mods = e.Item.Item.Mods;
            for (int m = 0; m < mods.Count; m++)
                if (catalog.Mods.ByHash(mods[m].Hash)?.Tags.Contains("life") == true)
                { lifeModifier = true; break; }
        }
        flags[NoOtherLifeModifiersCondition] = !lifeModifier;
    }
}
