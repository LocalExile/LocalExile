using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// A box's rarity and its explicit mods. The mods are the Mods rows of the chest domain (Domain 4) that are prefixes or
/// suffixes (GenerationType 1, 2), rolled as items roll theirs: by spawn weight against the Chests row's tags and
/// "default" (the first listed tag the box has decides), at most the area level, one mod per family, within the Rarity
/// table's counts (magic 1–2 mods, at most 1 prefix and 1 suffix; rare 4–6, at most 3 and 3, while the pool lasts).
/// <c>map_strongbox_mod_effect_+%</c> scales the rolled values of the mods' ranged stats.
/// </summary>
public static class StrongboxMods
{
    /// <summary>A box's base chances to be rare, else magic, in percent. INVENTED (our own values, not the game's): no
    /// client table gives a chest's rarity odds (Strongboxes, Chests, Rarity and ChestEffects have none) and no public
    /// source states them. Constants rather than settings: no other module exposes its drop odds as a setting.</summary>
    public const int RareChance = 10, MagicChance = 35;

    /// <summary>The chest domain (Mods.Domain).</summary>
    public const int ChestDomain = 4;

    /// <summary>Normal 0, magic 1 or rare 2: the base chances with <c>map_strongboxes_chance_to_be_rare_+%</c>, raised to
    /// <c>map_strongboxes_minimum_rarity</c> (1 magic, 2 rare; 3 unique is taken as rare, as no unique box is made) and to
    /// rare by <c>map_strongboxes_at_least_rare</c>.</summary>
    public static byte RollRarity(MapMods mods, Random rng)
    {
        Func<string, int> stat = mods.Get;
        double rare = RareChance * Math.Max(0, 100 + stat("map_strongboxes_chance_to_be_rare_+%")) / 100.0;
        double roll = rng.NextDouble() * 100;
        int rarity = roll < rare ? 2 : roll < rare + MagicChance ? 1 : 0;
        int minimum = Math.Clamp(stat("map_strongboxes_minimum_rarity"), 0, 2);
        if (stat("map_strongboxes_at_least_rare") != 0) minimum = 2;
        return (byte)Math.Max(rarity, minimum);
    }

    static readonly Lazy<Dictionary<int, (int Min, int Max, int Prefixes, int Suffixes)>> Counts = new(() =>
    {
        var counts = new Dictionary<int, (int, int, int, int)>();
        foreach (var r in ClientTables.TableOrEmpty("Rarity"))
            counts[r.Index] = (r.IntOrNull("MinMods") ?? 0, r.IntOrNull("MaxMods") ?? 0, r.IntOrNull("MaxPrefix") ?? 0, r.IntOrNull("MaxSuffix") ?? 0);
        return counts;
    });

    /// <summary>The explicit mods of a box of this rarity, at this area level, scaled by the mod effect.</summary>
    public static List<ItemMod> Roll(ModCatalog catalog, StrongboxCatalog.Definition box, byte rarity, int level, MapMods stat, Random rng)
    {
        if (rarity == 0 || !box.Rollable || !Counts.Value.TryGetValue(rarity, out var limits) || limits.Max <= 0) return [];
        return Add(catalog, box, [], rarity, rng.Next(Math.Max(1, limits.Min), limits.Max + 1), level, stat, rng);
    }

    /// <summary>The box's mods with up to <paramref name="more"/> more rolled onto them (within the rarity's prefix and
    /// suffix limits and the families the mods already take; fewer when the pool runs out), prefixes first.</summary>
    public static List<ItemMod> Add(ModCatalog catalog, StrongboxCatalog.Definition box, IReadOnlyList<ItemMod> mods, byte rarity, int more,
        int level, MapMods stat, Random rng)
    {
        var result = mods.ToList();
        if (!box.Rollable || !Counts.Value.TryGetValue(rarity, out var limits)) return result;
        var have = mods.Select(m => catalog.ByHash(m.Hash)).OfType<ModCatalog.Mod>().ToList();
        var pool = catalog.All.Where(m => m.Domain == ChestDomain && m.GenerationType is 1 or 2 && !m.EssenceOnly
            && m.Level <= level && m.Stats.Count > 0 && m.WeightFor(box.Tags) > 0).ToList();
        int prefixes = have.Count(m => m.GenerationType == 1), suffixes = have.Count(m => m.GenerationType == 2);
        var families = have.SelectMany(m => m.Families).ToHashSet();
        int effect = stat.Get("map_strongbox_mod_effect_+%");
        for (int added = 0; added < more && result.Count < limits.Max; added++)
        {
            var open = pool.Where(m => !have.Contains(m) && !m.Families.Any(families.Contains)
                && (m.GenerationType == 1 ? prefixes < limits.Prefixes : suffixes < limits.Suffixes)).ToList();
            long total = open.Sum(m => (long)m.WeightFor(box.Tags));
            if (total <= 0) break;
            long pick = (long)(rng.NextDouble() * total);
            var mod = open.FirstOrDefault(m => (pick -= m.WeightFor(box.Tags)) < 0) ?? open[^1];
            have.Add(mod);
            families.UnionWith(mod.Families);
            if (mod.GenerationType == 1) prefixes++; else suffixes++;
            result.Add(Scaled(mod, mod.Roll(ModSection.Explicit, rng), effect));
        }
        // Prefixes first, as an item lists them.
        return [.. result.OrderBy(m => catalog.ByHash(m.Hash)?.GenerationType ?? 0)];
    }

    /// <summary><c>map_strongbox_mod_effect_+%</c> on a rolled mod: its ranged values scaled.</summary>
    static ItemMod Scaled(ModCatalog.Mod mod, ItemMod item, int effect) => effect == 0 ? item : item with
    {
        Values = [.. item.Values.Select((v, i) => mod.RangeAt(i) is var (min, max) && min != max
            ? (int)Math.Clamp((long)v * Math.Max(0, 100 + effect) / 100, int.MinValue, int.MaxValue) : v)],
    };

    /// <summary>The mods with their values rolled again (a Divine Orb), scaled by the mod effect.</summary>
    public static List<ItemMod> Reroll(ModCatalog catalog, IReadOnlyList<ItemMod> mods, MapMods stat, Random rng) =>
        [.. mods.Select(m => catalog.ByHash(m.Hash) is { } mod ? Scaled(mod, mod.Roll(ModSection.Explicit, rng), stat.Get("map_strongbox_mod_effect_+%")) : m)];

    /// <summary>The rarity's most mods (the Rarity table's MaxMods), 0 for normal.</summary>
    public static int MaxMods(byte rarity) => Counts.Value.TryGetValue(rarity, out var l) ? l.Max : 0;

    /// <summary>A box's own mods (its Chests implicits, then its rolled explicits), each with its values.</summary>
    public static IEnumerable<(ModCatalog.Mod Mod, IReadOnlyList<int> Values)> Lines(ModCatalog catalog, StrongboxCatalog.Definition box,
        IReadOnlyList<ItemMod> mods)
    {
        foreach (var m in box.Mods.Concat(mods))
            if (catalog.ByHash(m.Hash) is { } mod) yield return (mod, m.Values);
    }

    /// <summary>One stat of a mod line, 0 when the mod has none.</summary>
    public static int Value((ModCatalog.Mod Mod, IReadOnlyList<int> Values) line, string id)
    {
        for (int i = 0; i < line.Mod.Stats.Count && i < line.Values.Count; i++)
            if (line.Mod.Stats[i].Id == id) return line.Values[i];
        return 0;
    }

    /// <summary>A box's inherent bonus for its rarity: the client's hidden chest mods ChestMagic1/2, ChestRare1/2 and
    /// ChestUnique1/2 (<c>chest_hidden_item_quantity_+%</c> and <c>chest_hidden_item_rarity_+%</c>: 50, 100 and 150), which
    /// every non-normal box has whatever its other mods (poewiki, Strongbox: "Non-normal rarity strongboxes inherently have
    /// increased item quantity and item rarity bonuses based on the strongbox's rarity").</summary>
    public static int RarityStat(ModCatalog catalog, byte rarity, string id)
    {
        string? name = rarity switch { 1 => "ChestMagic", 2 => "ChestRare", 3 => "ChestUnique", _ => null };
        if (name is null) return 0;
        int total = 0;
        foreach (var mod in catalog.All.Where(m => m.Id == name + "1" || m.Id == name + "2"))
            for (int i = 0; i < mod.Stats.Count; i++)
                if (mod.Stats[i].Id == id) total += mod.RangeAt(i).Min;
        return total;
    }

    /// <summary>The sum of a stat over a box's own mods, by stat id.</summary>
    public static int Stat(ModCatalog catalog, StrongboxCatalog.Definition box, IReadOnlyList<ItemMod> mods, string id) =>
        (int)Math.Clamp(Lines(catalog, box, mods).Sum(l => (long)Value(l, id)), int.MinValue, int.MaxValue);
}
