using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Official-style divination card turn-in (3.17+, no Navali): a complete stack right-clicked in the inventory is
/// consumed and its reward is placed in the main inventory. Stack sizes and rewards come from data/divination-cards.json.
/// </summary>
static class DivinationCards
{
    internal sealed record Reward(int Stack, string Kind, string? Item = null, int Count = 1, bool Corrupted = false,
        bool TwoImplicit = false, int Quality = 0, int GemLevel = 0, bool SixLink = false, int ItemLevel = 0);

    /// <summary>Mods.dat GenerationType of Vaal / corruption implicits.</summary>
    const int CorruptedImplicit = 5;

    static readonly Lazy<Dictionary<string, Reward>> Table = new(Load);
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static readonly string[] Exceptional = ["Empower", "Enhance", "Enlighten"];
    static readonly string[] AwakenedExceptional = ["Awakened Empower", "Awakened Enhance", "Awakened Enlighten"];

    internal static IReadOnlyDictionary<string, Reward> All => Table.Value;

    internal static Reward? Of(string name) => Table.Value.GetValueOrDefault(name);

    /// <summary>How many of this card make a set, or null when it is not a card in the table (catalogue MaxStack stays).</summary>
    internal static ushort? StackOf(string name) =>
        Table.Value.TryGetValue(name, out var reward) ? (ushort)Math.Clamp(reward.Stack, 1, ushort.MaxValue) : null;

    /// <summary>The items a complete set yields. Null when the reward cannot be created from this catalogue.</summary>
    internal static IReadOnlyList<Item>? Create(Reward reward, ItemCatalog items, UniqueCatalog uniques, ItemRolls rolls,
        Random rng, int depth = 0)
    {
        if (depth > 4) return null;
        return reward.Kind switch
        {
            "void" => Create(PickOther(reward, rng), items, uniques, rolls, rng, depth + 1),
            "random_card" => CardItem(items, rng) is { } card ? [card] : null,
            "random_unique" => UniqueItem(items, uniques, rng, map: false) is { } unique
                && items.ByHash(unique.TypeHash) is { } uniqueType
                ? [Finish(unique, uniqueType, reward, rolls, items, rng)] : null,
            "random_unique_map" => UniqueItem(items, uniques, rng, map: true) is { } map
                && items.ByHash(map.TypeHash) is { } mapType
                ? [Finish(map, mapType, reward, rolls, items, rng)] : null,
            _ => Named(reward, items, uniques, rolls, rng),
        };
    }

    static IReadOnlyList<Item>? Named(Reward reward, ItemCatalog items, UniqueCatalog uniques, ItemRolls rolls, Random rng)
    {
        if (reward.Kind is "card" or "divination")
            return CardItem(items, rng, reward.Item) is { } card
                ? Copies(card, items.ByHash(card.TypeHash)!, reward) : null;
        if (reward.Kind == "gem")
            return GemItem(items, reward, rng) is { } gem && items.ByHash(gem.TypeHash) is { } gemType
                ? [Finish(gem, gemType, reward, rolls, items, rng)] : null;
        if (reward.Kind == "unique")
        {
            if (reward.Item is { Length: > 0 } name && UniqueNamed(uniques, name, rng) is { } created
                && items.ByHash(created.TypeHash) is { } uniqueType)
                return Copies(Finish(created, uniqueType, reward, rolls, items, rng), uniqueType, reward);
            return Named(reward with { Kind = "normal" }, items, uniques, rolls, rng);
        }
        if (BaseOf(items, reward.Item, rng) is not { } type) return null;
        var item = Finish(ItemCatalog.Create(type, random: rng), type, reward, rolls, items, rng);
        return Copies(item, type, reward);
    }

    static IReadOnlyList<Item> Copies(Item item, ItemCatalog.BaseType type, Reward reward)
    {
        int n = reward.Count > 0 ? reward.Count : 1;
        if (n <= 1) return [item];
        if (type.Stackable || StackOf(type.Name) is > 1)
            return [item with { StackSize = (ushort)Math.Min(n, ushort.MaxValue) }];
        return [.. Enumerable.Range(0, n).Select(_ => item)];
    }

    static Item Finish(Item item, ItemCatalog.BaseType type, Reward reward, ItemRolls rolls, ItemCatalog items, Random rng)
    {
        if (reward.ItemLevel > 0) item = item with { ItemLevel = (byte)Math.Clamp(reward.ItemLevel, 1, 100) };
        if (reward.Quality > 0) item = item with { Quality = (byte)Math.Clamp(reward.Quality, 0, 23) };
        if (reward.Kind is "magic" && item.Rarity == ItemRarity.Normal)
            item = rolls.Transmute(item, rng) ?? item with { Rarity = ItemRarity.Magic, Identified = true };
        else if (reward.Kind is "rare" && item.Rarity == ItemRarity.Normal)
            item = rolls.Alchemy(item, rng) ?? item with { Rarity = ItemRarity.Rare, Identified = true };
        if (reward.SixLink) item = SixLinked(item, type);
        if (reward.TwoImplicit || reward.Corrupted)
            item = CorruptReward(item, type, items, rng, reward.TwoImplicit);
        return item with { Identified = true };
    }

    /// <summary>
    /// Official card text: "Corrupted" is the Vaal flag; "Two-Implicit Corrupted" also adds Vaal implicits until the
    /// item has two implicits (the original base/unique implicit is kept). Gems and types without Mods only get the flag.
    /// </summary>
    static Item CorruptReward(Item item, ItemCatalog.BaseType type, ItemCatalog items, Random rng, bool twoImplicit)
    {
        item = item with { Corrupted = true };
        if (!twoImplicit || !type.Components.Contains("Mods") || type.Components.Contains("SkillGem"))
            return item;
        int have = item.Mods.Count(m => m.Section == ModSection.Implicit);
        int add = Math.Max(1, 2 - have);
        var mods = item.Mods.ToList();
        var used = mods.Select(m => items.Mods.ByHash(m.Hash)).OfType<ModCatalog.Mod>()
            .SelectMany(m => m.Families).ToHashSet();
        for (int n = 0; n < add; n++)
        {
            var pool = items.Mods.All
                .Where(m => m.GenerationType == CorruptedImplicit && m.Domain == type.ModDomain
                    && m.WeightFor(type.Tags) > 0 && !m.Families.Any(used.Contains))
                .Select(m => (Mod: m, Weight: m.WeightFor(type.Tags)))
                .ToList();
            if (pool.Count == 0) break;
            long roll = rng.NextInt64(pool.Sum(p => (long)p.Weight));
            var chosen = pool[^1].Mod;
            foreach (var (mod, weight) in pool)
            {
                if (roll < weight) { chosen = mod; break; }
                roll -= weight;
            }
            int at = mods.FindLastIndex(m => m.Section == ModSection.Implicit) + 1;
            if (at == 0)
            {
                int firstOther = mods.FindIndex(m => m.Section != ModSection.Implicit);
                at = firstOther >= 0 ? firstOther : mods.Count;
            }
            mods.Insert(at, chosen.Roll(ModSection.Implicit, rng));
            foreach (int family in chosen.Families) used.Add(family);
        }
        return item with { Mods = mods };
    }

    static Item SixLinked(Item item, ItemCatalog.BaseType type)
    {
        int n = Math.Min(6, CurrencyEffects.SocketCapacity(item, type));
        if (n < 2) return item;
        return item with
        {
            Sockets = [.. Enumerable.Range(0, n).Select(_ => new ItemSocket(SocketColour.White))],
            Links = [(byte)n],
        };
    }

    static Item? UniqueNamed(UniqueCatalog uniques, string name, Random rng)
    {
        var key = UniqueCatalog.NameKey(name);
        var definition = uniques.All.FirstOrDefault(d => UniqueCatalog.NameKey(d.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
        return definition is { } found ? uniques.Create(found, rng) : null;
    }

    static Item? UniqueItem(ItemCatalog items, UniqueCatalog uniques, Random rng, bool map)
    {
        var pool = uniques.All.Where(d => !d.Corrupted && !d.Name.StartsWith("Replica ", StringComparison.Ordinal)).ToList();
        if (map)
        {
            var maps = pool.Where(d => items.Find(d.BaseTypeId) is { } t && CurrencyEffects.IsMap(t)).ToList();
            if (maps.Count > 0) pool = maps;
        }
        for (int i = 0; i < 24 && pool.Count > 0; i++)
            if (uniques.Create(pool[rng.Next(pool.Count)], rng) is { } item) return item;
        return null;
    }

    static Item? CardItem(ItemCatalog items, Random rng, string? name = null)
    {
        if (name is { Length: > 0 } && items.Find(name) is { Class: "DivinationCard", Supported: true } named)
            return ItemCatalog.Create(named);
        var cards = items.All.Where(t => t.Class == "DivinationCard" && t.Supported && t.Name.Length > 0).ToList();
        return cards.Count == 0 ? null : ItemCatalog.Create(cards[rng.Next(cards.Count)]);
    }

    static Item? GemItem(ItemCatalog items, Reward reward, Random rng)
    {
        string name = reward.Item ?? "Gem";
        var hit = PickGem(items, name, rng);
        if (hit is not { } gem) return null;
        var type = items.Gem(gem.Type);
        if (type is null) return null;
        int level = reward.GemLevel > 0 ? reward.GemLevel : 1;
        int xp = level > type.MaxLevel ? -1 : type.ExperienceFor(level);
        return ItemCatalog.Create(gem.Type) with { GemExperience = xp, GemVariant = gem.Variant.Index };
    }

    static (ItemCatalog.BaseType Type, ItemCatalog.GemVariant Variant)? PickGem(ItemCatalog items, string name, Random rng)
    {
        if (name is "Exceptional Support Gem" or "Empower, Enhance or Enlighten")
            return NamedGem(items, Exceptional[rng.Next(Exceptional.Length)]);
        if (name is "Awakened Exceptional Gem")
            return NamedGem(items, AwakenedExceptional[rng.Next(AwakenedExceptional.Length)]);
        if (!GenericGem(name) && items.FindGem(name) is { } exact) return exact;
        var all = items.All.Select(t => (Type: t, Gem: items.Gem(t))).Where(x => x.Gem is not null)
            .SelectMany(x => x.Gem!.Variants.Where(v => v.Creatable).Select(v => (x.Type, Variant: v))).ToList();
        if (all.Count == 0) return null;
        bool support = name.Contains("Support", StringComparison.OrdinalIgnoreCase);
        bool vaal = name.Contains("Vaal", StringComparison.OrdinalIgnoreCase);
        bool aura = name.Contains("Aura", StringComparison.OrdinalIgnoreCase);
        bool minion = name.Contains("Minion", StringComparison.OrdinalIgnoreCase);
        bool golem = name.Contains("Golem", StringComparison.OrdinalIgnoreCase);
        bool chaos = name.Contains("Chaos", StringComparison.OrdinalIgnoreCase);
        bool trap = name.Contains("Trap", StringComparison.OrdinalIgnoreCase);
        bool transfigured = name.Contains("Transfigured", StringComparison.OrdinalIgnoreCase);
        var filtered = all.Where(g =>
            (!support || g.Type.Name.Contains("Support", StringComparison.OrdinalIgnoreCase) || items.Gem(g.Type)!.IsSupport)
            && (!vaal || g.Variant.Name.Contains("Vaal", StringComparison.OrdinalIgnoreCase))
            && (!aura || g.Variant.Name.Contains("Aura", StringComparison.OrdinalIgnoreCase) || g.Type.Id.Contains("Aura"))
            && (!minion || g.Variant.Name.Contains("Minion", StringComparison.OrdinalIgnoreCase) || g.Type.Id.Contains("Minion")
                || g.Variant.Name.Contains("Skeleton", StringComparison.OrdinalIgnoreCase) || g.Variant.Name.Contains("Zombie", StringComparison.OrdinalIgnoreCase)
                || g.Variant.Name.Contains("Spectre", StringComparison.OrdinalIgnoreCase) || g.Variant.Name.Contains("Raging Spirit", StringComparison.OrdinalIgnoreCase))
            && (!golem || g.Variant.Name.Contains("Golem", StringComparison.OrdinalIgnoreCase))
            && (!chaos || g.Variant.Name.Contains("Chaos", StringComparison.OrdinalIgnoreCase) || g.Type.Id.Contains("Chaos"))
            && (!trap || g.Variant.Name.Contains("Trap", StringComparison.OrdinalIgnoreCase) || g.Type.Id.Contains("Trap"))
            && (!transfigured || g.Variant.Index > 0)).ToList();
        var pool = filtered.Count > 0 ? filtered : all;
        return pool[rng.Next(pool.Count)];
    }

    static (ItemCatalog.BaseType Type, ItemCatalog.GemVariant Variant)? NamedGem(ItemCatalog items, string name) =>
        items.FindGem(name);

    static bool GenericGem(string name) =>
        name is "Gem" or "Support Gem" or "Spell Gem" or "Minion Gem" or "Aura Gem" or "Vaal Gem" or "Golem Gem"
            or "Chaos Gem" or "Trap Gem" or "Transfigured Gem" or "Transfigured Golem Gem"
            or "Exceptional Support Gem" or "Awakened Exceptional Gem" or "Empower, Enhance or Enlighten";

    static ItemCatalog.BaseType? BaseOf(ItemCatalog items, string? name, Random rng)
    {
        if (name is not { Length: > 0 }) return RandomEquipment(items, rng, null);
        if (items.All.FirstOrDefault(t => t.Supported && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } named)
            return named;
        var byClass = items.All.Where(t => t.Supported && t.Class.Equals(name, StringComparison.OrdinalIgnoreCase)
            && t.DropLevel > 0).ToList();
        if (byClass.Count > 0)
        {
            var droppable = byClass.Where(ItemDrops.Droppable).ToList();
            var pool = droppable.Count > 0 ? droppable : byClass;
            return pool[rng.Next(pool.Count)];
        }
        return items.Find(name) is { Supported: true } fallback ? fallback : RandomEquipment(items, rng, name);
    }

    static ItemCatalog.BaseType? RandomEquipment(ItemCatalog items, Random rng, string? hint)
    {
        var pool = items.All.Where(t => t.Supported && ItemDrops.EquipmentClasses.Contains(t.Class) && ItemDrops.Droppable(t)).ToList();
        if (hint is { Length: > 0 })
        {
            var narrowed = pool.Where(t => t.Class.Contains(hint, StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (narrowed.Count > 0) pool = narrowed;
        }
        return pool.Count == 0 ? null : pool[rng.Next(pool.Count)];
    }

    static Reward PickOther(Reward self, Random rng)
    {
        var others = Table.Value.Values.Where(r => r.Kind is not "void" and not "random_card").ToList();
        return others.Count == 0 ? self : others[rng.Next(others.Count)];
    }

    static Dictionary<string, Reward> Load()
    {
        string path = Path.Combine("data", "divination-cards.json");
        if (!File.Exists(path))
        {
            Console.WriteLine("[gateway] data/divination-cards.json is missing: divination card turn-in is disabled");
            return new(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, Reward>>(File.ReadAllBytes(path), Json);
            return table is null ? new(StringComparer.OrdinalIgnoreCase) : new(table, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] data/divination-cards.json: {ex.Message}");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
