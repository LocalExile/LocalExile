using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>A unique jewel from the global (non-radius) catalogue, max-rolled for predictable testing. Null when the
    /// name is not in the catalogue or a mod cannot be resolved from the current game data.</summary>
    public static Item? CreateUniqueJewel(string name)
    {
        if (UniqueJewelCatalogue.Find(name) is not { } entry || Items.Find(entry.Base) is not { } baseType) return null;
        var mods = new List<ItemMod>();
        for (int i = 0; i < entry.Mods.Length; i++)
        {
            if (Items.Mods.ById(entry.Mods[i]) is not { } mod || mod.Stats.Count == 0) continue;
            int? fixedValue = i < entry.Values.Length ? entry.Values[i] : null;
            var values = mod.MaxValues().ToArray();
            if (fixedValue is int value) values[0] = value;
            mods.Add(new ItemMod(ModSection.Explicit, mod.Hash, values));
        }
        if (mods.Count == 0) return null;
        return ItemCatalog.Create(baseType) with
        {
            Rarity = ItemRarity.Unique,
            Corrupted = entry.Corrupted,
            ItemLevel = (byte)Math.Clamp(entry.Level, 1, 100),
            VisualIdentity = UniqueJewelVisuals.Of(entry.Name) ?? baseType.VisualIdentity,
            NameWords = RadiusJewelCatalogue.WordHash(entry.Name, "data") is uint word ? [word] : [],
            Mods = mods,
        };
    }
}
