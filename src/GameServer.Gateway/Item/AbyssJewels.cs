using GameServer.Core.Protocol.Instance;

public static class AbyssJewels
{
    public const string StygianVise = "Metadata/Items/Belts/BeltAbyss";

    public static bool Is(Item jewel, ItemCatalog catalog) => catalog.ByHash(jewel.TypeHash)?.Class == "AbyssJewel";

    public static Item StygianSockets(Item item, IReadOnlyDictionary<string, int> local)
    {
        int count = Math.Clamp(local.GetValueOrDefault("local_has_X_abyss_sockets"), 0, SocketLayout.MaximumSockets);
        return item with { Sockets = [.. Enumerable.Repeat(new ItemSocket(SocketColour.Abyss), count)], Links = null };
    }

    public static Item EquipmentSockets(Item item, ItemCatalog.BaseType type, ItemCatalog catalog,
        IReadOnlyDictionary<string, int> local, Random random)
    {
        int capacity = CurrencyEffects.SocketCapacity(item, type);
        int eyes = Math.Clamp(local.GetValueOrDefault("local_has_X_abyss_sockets"), 0, capacity);
        var sockets = new List<ItemSocket>();
        if (!local.ContainsKey("local_cannot_have_non_abyssal_sockets"))
        {
            var ordinary = catalog.DefaultSockets(type, item.ItemLevel, item.Quality, random,
                local.ContainsKey("local_sockets_cannot_be_white")).Sockets;
            sockets.AddRange(ordinary.Take(Math.Max(0, Math.Min(capacity, ordinary.Count) - eyes)));
        }
        int normal = sockets.Count;
        sockets.AddRange(Enumerable.Repeat(new ItemSocket(SocketColour.Abyss), eyes));
        return item with { Sockets = sockets, Links = normal > 1 ? [(byte)normal, .. Enumerable.Repeat((byte)1, eyes)] : null };
    }

    // Equipment and its socket contents share activation. The projected rolls never change the saved jewel.
    public static IEnumerable<Item> Sources(IEnumerable<Item> active, ItemCatalog catalog)
    {
        foreach (var item in active)
        {
            yield return item;
            foreach (var socket in item.Sockets)
            {
                if (socket is not { Colour: SocketColour.Abyss, Gem: { } jewel } || !Is(jewel, catalog)) continue;
                int effect = ItemModStats.Local(item, catalog.Mods).GetValueOrDefault("local_socketed_abyss_jewel_effect_+%");
                yield return effect == 0 ? jewel : jewel with
                {
                    Mods = [.. jewel.Mods.Select(mod => catalog.Mods.ByHash(mod.Hash) is { } row ? mod with
                    {
                        Values = [.. mod.Values.Select((value, n) => n < row.Stats.Count && !row.Stats[n].Local
                            ? RadiusJewelCatalogue.ScaleEffect(value, effect) : value)],
                    } : mod)],
                };
            }
        }
    }

    public static void Count(IReadOnlyList<Item> active, ItemCatalog catalog, StatCatalog stats, Dictionary<uint, int> sources)
    {
        var kinds = new HashSet<string>();
        void Add(string stat)
        {
            if (stats.Key(stat) is uint key) sources[key] = sources.GetValueOrDefault(key) + 1;
        }
        foreach (var item in active)
        {
            if (catalog.ByHash(item.TypeHash) is not { Class: "AbyssJewel" } type) continue;
            Add("number_of_abyss_jewels");
            if (item.Rarity is ItemRarity.Magic or ItemRarity.Rare or ItemRarity.Unique)
                Add($"number_of_{item.Rarity.ToString().ToLowerInvariant()}_abyss_jewels");
            string? kind = type.Id switch
            {
                "Metadata/Items/Jewels/JewelAbyssMelee" => "melee",
                "Metadata/Items/Jewels/JewelAbyssRanged" => "ranged",
                "Metadata/Items/Jewels/JewelAbyssCaster" => "caster",
                "Metadata/Items/Jewels/JewelAbyssSummoner" => "minion",
                _ => null,
            };
            if (kind is null) continue;
            Add($"number_of_{kind}_abyss_jewels");
            if (kinds.Add(kind)) Add("number_of_abyss_jewel_types");
        }
    }
}
