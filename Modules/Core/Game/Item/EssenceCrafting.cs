using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;


/// <summary>Essences.dat's exact class-specific affix keys, resolved against this client's Mods.dat.</summary>
public sealed class EssenceCrafting(ItemCatalog catalog, ItemRolls rolls, string folder = "data")
{
    public sealed record Essence(uint Hash, string Id, int Tier, int ModLevelCap, IReadOnlyDictionary<string, int> ModRows)
    {
        // Tier zero with item modifiers is the Essence of Desolation, not the Remnant of Corruption.
        public bool ReforgesRare => Tier >= 5 || Tier == 0;
    }

    static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["Helmet"] = "Helmet_ModsKey", ["Body Armour"] = "BodyArmour_ModsKey",
        ["Boots"] = "Boots_ModsKey", ["Gloves"] = "Gloves_ModsKey", ["Bow"] = "Bow_ModsKey",
        ["Wand"] = "Wand_ModsKey", ["Staff"] = "Staff_ModsKey", ["Warstaff"] = "Staff_ModsKey",
        ["Two Hand Sword"] = "TwoHandSword_ModsKey", ["Two Hand Axe"] = "TwoHandAxe_ModsKey",
        ["Two Hand Mace"] = "TwoHandMace_ModsKey", ["Claw"] = "Claw_ModsKey",
        ["Dagger"] = "Dagger_ModsKey", ["Rune Dagger"] = "Dagger_ModsKey",
        ["One Hand Sword"] = "OneHandSword_ModsKey", ["Thrusting One Hand Sword"] = "OneHandThrustingSword_ModsKey",
        ["One Hand Axe"] = "OneHandAxe_ModsKey", ["One Hand Mace"] = "OneHandMace_ModsKey",
        ["Sceptre"] = "Sceptre_ModsKey", ["Belt"] = "Belt_ModsKey", ["Amulet"] = "Amulet_ModsKey",
        ["Ring"] = "Ring_ModsKey", ["Shield"] = "Shield_ModsKey",
        // The schema has no separate non-display Quiver column; this is its actual guaranteed mod.
        ["Quiver"] = "Display_Quiver_ModsKey",
    };

    readonly Lazy<IReadOnlyList<Essence>> _all = new(() => Load(catalog, folder));
    public IReadOnlyList<Essence> All => _all.Value;
    public bool Contains(uint hash) => All.Any(e => e.Hash == hash);
    public ModCatalog.Mod? Guaranteed(Essence essence, ItemCatalog.BaseType type) =>
        essence.ModRows.TryGetValue(type.Class, out int row) ? catalog.Mods.Row(row) : null;

    public Item? Apply(uint essenceHash, Item item, Random random)
    {
        var essence = All.FirstOrDefault(e => e.Hash == essenceHash);
        if (essence is null || item.Corrupted || item.Mirrored || !item.Components.Contains("Mods") ||
            item.Rarity != ItemRarity.Normal && !(item.Rarity == ItemRarity.Rare && essence.ReforgesRare) ||
            catalog.ByHash(item.TypeHash) is not { } type || Guaranteed(essence, type) is not { } forced ||
            forced.Domain != type.ModDomain ||
            forced.GenerationType is not (ItemRolls.Prefix or ItemRolls.Suffix)) return null;

        var fractured = item.FracturedIndexes();
        var fixedMods = item.Mods.Where((m, i) => fractured.Contains(i)).Select(m => catalog.Mods.ByHash(m.Hash)).OfType<ModCatalog.Mod>().ToList();
        if (fixedMods.Any(m => m.Hash == forced.Hash || m.Families.Intersect(forced.Families).Any()) ||
            fixedMods.Count(m => m.GenerationType == forced.GenerationType) >= 3) return null;
        // Modern essences refuse meta-crafted items, rather than pretending to honour the protected affixes.
        if (item.Mods.Where(m => m.Section == ModSection.Explicit).Any(m => catalog.Mods.ByHash(m.Hash)?.Stats.Any(s =>
            s.Id is "item_generation_cannot_change_prefixes" or "item_generation_cannot_change_suffixes" or
                "item_generation_cannot_roll_attack_affixes" or "item_generation_cannot_roll_caster_affixes") == true)) return null;

        return ItemRolls.ClearForesee(rolls.Roll(item, ItemRarity.Rare, random, guaranteed: forced,
            maxModLevel: essence.ModLevelCap > 0 ? essence.ModLevelCap : null) with { Identified = true });
    }

    static IReadOnlyList<Essence> Load(ItemCatalog catalog, string folder)
    {
        if (!ClientTables.Has("Essences")) return [];
        var doc = ClientTables.Table("Essences");
        var result = new List<Essence>();
        foreach (var row in doc)
        {
            if (catalog.Row(row.Int("BaseItemTypesKey")) is not { } currency) continue;
            var mods = new Dictionary<string, int>();
            foreach (var (itemClass, column) in Columns)
                if (row.IntOrNull(column) is int key)
                    mods.Add(itemClass, key);
            if (mods.Count == 0) continue; // Remnants target imprisoned monsters, not equipment.
            result.Add(new(currency.Hash, currency.Id, row.Int("Level"),
                row.Int("ItemLevelRestriction"), mods));
        }
        return result;
    }
}
