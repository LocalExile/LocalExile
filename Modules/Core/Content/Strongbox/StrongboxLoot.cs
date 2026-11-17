using GameServer.Core.Protocol.Instance;
using static GameServer.Modules.Strongbox.StrongboxCatalog;

namespace GameServer.Modules.Strongbox;

/// <summary>
/// What an opened box drops: its base item count (<see cref="Entry.Items"/>) × (100 + <c>map_strongbox_item_quantity_+%</c>)
/// / 100 × the league's item drop multiplier (a fraction is a chance of one more), of the kind its Chests row names, at the
/// area level, through the server's drop pipeline (<see cref="ItemDrops"/>, the item rolls). A box of no kind is the general
/// table (<see cref="ItemDrops.Container"/>, with the chest's hidden rarity). The atlas's "… are duplicated" stats double
/// the currency, maps, divination cards or gems.
/// </summary>
public static class StrongboxLoot
{
    // Artisan's: the quality currencies of the general currency table (ItemDrops.Currency), at their weights there.
    static readonly HashSet<string> QualityCurrency = new(StringComparer.Ordinal)
    {
        "Metadata/Items/Currency/CurrencyArmourQuality", "Metadata/Items/Currency/CurrencyWeaponQuality",
        "Metadata/Items/Currency/CurrencyFlaskQuality", "Metadata/Items/Currency/CurrencyGemQuality",
    };

    static readonly HashSet<string> ArmourClasses = new(StringComparer.Ordinal) { "Body Armour", "Helmet", "Gloves", "Boots", "Shield" };
    static readonly HashSet<string> JewelleryClasses = new(StringComparer.Ordinal) { "Ring", "Amulet" };

    public static List<ItemDrops.Drop> Roll(ClientState conn, Entry box, int level, IReadOnlyDictionary<uint, int> stats, Random rng)
    {
        int Stat(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var catalog = ItemsFor(conn);
        var drops = DropsFor(conn);
        var rolls = RollsFor(conn);
        var rules = LeagueFor(conn).Rules;
        level = Math.Clamp(level, 1, 100);
        double quantity = box.Items * Math.Max(0, 100 + Stat("map_strongbox_item_quantity_+%")) / 100.0 * Math.Max(0, rules.ItemDropMultiplier);
        int count = Math.Max(1, (int)Math.Floor(quantity) + (rng.NextDouble() < quantity - Math.Floor(quantity) ? 1 : 0));
        var result = new List<ItemDrops.Drop>();
        if (box.Kind == Contents.General)
            result.AddRange(drops.Container(level, count, box.HiddenRarity, rng));
        else
        {
            var gems = box.Kind == Contents.Gem ? GemSources.DropPool(catalog) : null;
            ItemCatalog.BaseType[] bases = box.Kind switch
            {
                Contents.Armour => Bases(t => ArmourClasses.Contains(t.Class)),
                Contents.Weapon => Bases(t => t.Components.Contains("Weapon")),
                Contents.Jewellery => Bases(t => JewelleryClasses.Contains(t.Class)),
                Contents.Gem => [.. catalog.All.Where(t => t.Supported && t.DropLevel <= level && gems!.Contains(t.Row))],
                _ => [],
            };
            var currency = box.Kind is Contents.Currency or Contents.QualityCurrency
                ? ItemDrops.Currency.Where(c => box.Kind == Contents.Currency || QualityCurrency.Contains(c.Id))
                    .Select(c => (Type: catalog.Find(c.Id), c.Weight)).Where(c => c.Type is { Supported: true } && c.Type.DropLevel <= level)
                    .Select(c => (Type: c.Type!, c.Weight)).ToArray()
                : [];
            for (int i = 0; i < count; i++)
                if (One() is { } drop) result.Add(drop);

            ItemDrops.Drop? One()
            {
                switch (box.Kind)
                {
                    case Contents.Card: return drops.Card(rng, level);
                    case Contents.Scarab: return drops.Scarab(rng, level);
                    case Contents.Map: return Map();
                    case Contents.Currency or Contents.QualityCurrency:
                    {
                        double pick = rng.NextDouble() * currency.Sum(c => c.Weight);
                        foreach (var (type, weight) in currency)
                            if ((pick -= weight) <= 0) return new(ItemCatalog.Create(type) with { StackSize = 1 }, type);
                        return currency.Length > 0 ? new(ItemCatalog.Create(currency[^1].Type) with { StackSize = 1 }, currency[^1].Type) : null;
                    }
                    default:
                    {
                        if (bases.Length == 0) return null;
                        var type = bases[rng.Next(bases.Length)];
                        // A dropped gem is unlevelled (ItemDrops: its level comes from its experience).
                        if (box.Kind == Contents.Gem) return new(ItemCatalog.Create(type) with { ItemLevel = 1 }, type);
                        var (sockets, links) = catalog.DropSockets(type, level, rng);
                        var item = ItemCatalog.Create(type, sockets, links, random: rng) with { ItemLevel = (byte)level };
                        // A rare monster's rarity chances (ItemDrops.RarityChances), with the chest's hidden rarity.
                        var chances = ItemDrops.WithIncreasedRarity(ItemDrops.RarityChances(MonsterRarity.Rare), box.HiddenRarity,
                            rules.EquipmentRarityMultiplier);
                        if (rng.NextDouble() < chances.Rare) item = rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
                        else if (rng.NextDouble() < chances.Magic) item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = false };
                        return new(item, type);
                    }
                }
            }
        }
        // The atlas's duplication stats: each such item drops twice.
        var duplicated = result.Where(d =>
            d.Type.Class == "StackableCurrency" && Stat("map_strongboxes_currency_is_duplicated") != 0
            || d.Type.Class is "Map" or "MapKey" && Stat("map_strongboxes_maps_are_duplicated") != 0
            || d.Type.Class == "DivinationCard" && Stat("map_strongboxes_divination_cards_are_duplicated") != 0
            || catalog.Gem(d.Type) is not null && Stat("map_strongboxes_gems_are_duplicated") != 0).ToList();
        result.AddRange(duplicated);
        return result;

        ItemCatalog.BaseType[] Bases(Func<ItemCatalog.BaseType, bool> kind) =>
            [.. catalog.All.Where(t => ItemDrops.Droppable(t) && t.DropLevel <= level && ItemDrops.EquipmentClasses.Contains(t.Class) && kind(t))];

        // A generic map key of the open map's tier (the area level's in the campaign: level 68 is tier 1).
        ItemDrops.Drop? Map()
        {
            int tier = MapRunOf(conn)?.Area.Tier is int open and > 0 ? open : Math.Clamp(level - 67, 1, 16);
            if (MapDevices.Value.Atlas?.KeyOfTier(tier) is not uint hash || catalog.ByHash(hash) is not { Supported: true } type) return null;
            return new(ItemCatalog.Create(type) with { ItemLevel = (byte)level }, type);
        }
    }
}
