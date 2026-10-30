using GameServer.Core.Protocol.Instance;

/// <summary>
/// What equipped items' mods do (Agents/docs/protocol/13-items.md §2c). Every mod list counts (implicit, explicit,
/// enchant, scourge, hidden), identified or not, as in the game. A stat that Stats.dat marks IsLocal changes the item it
/// is on (<see cref="Local"/>: a weapon's critical chance, attack speed, accuracy …); every other stat is the
/// character's and is added to the passives' stats (<see cref="Global"/>), so it reaches the pools and the stats the
/// client shows the same way.
/// </summary>
static class ItemModStats
{
    /// <summary>The equipment rows whose items count: body armour, the first weapon set, helmet, amulet, rings, gloves,
    /// boots and belt (Inventories.dat rows 1-10). The second weapon set (14, 15) and the flasks (11) do not.</summary>
    public static readonly byte[] EquipmentRows = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>The summed values of the stats on these items' mods, by Stats.dat Id: global ones, or with
    /// <paramref name="local"/> the item's own.</summary>
    public static Dictionary<string, int> Sum(IEnumerable<Item> items, ModCatalog mods, bool local = false)
    {
        var sum = new Dictionary<string, int>();
        var attributes = new Dictionary<string, long>();
        foreach (var (id, value) in Values(items, mods, local))
            if (AttributeModifiers.IsInput(id)) attributes[id] = attributes.GetValueOrDefault(id) + value;
            else sum[id] = ArcaneMight.Combine(id, sum.GetValueOrDefault(id), value);
        // This int API is a bounded view for existing stat consumers, not an attribute calculation source.
        foreach (var (id, value) in attributes) sum[id] = AttributeModifiers.Signed(value);
        return sum;
    }

    static IEnumerable<(string Id, int Value)> Values(IEnumerable<Item> items, ModCatalog mods, bool local = false)
    {
        foreach (var mod in items.SelectMany(i => i.Mods))
            if (mods.ByHash(mod.Hash) is { } row)
                for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
                    if (row.Stats[s].Local == local) yield return (row.Stats[s].Id, mod.Values[s]);
    }

    /// <summary>Wide per-key attribute totals: retain them until complete raw-key replacement and bucket scaling.</summary>
    public static Dictionary<string, long> RawAttributes(IEnumerable<Item> items, ModCatalog mods)
    {
        var raw = new Dictionary<string, long>();
        foreach (var (id, value) in Values(items, mods))
            if (AttributeModifiers.IsInput(id)) raw[id] = raw.GetValueOrDefault(id) + value;
        return raw;
    }

    /// <summary>The character's stats from these equipped items, by client stat key (Stats.dat row + 1).</summary>
    public static Dictionary<uint, int> Global(IEnumerable<Item> equipped, ModCatalog mods, StatCatalog stats)
    {
        var result = new Dictionary<uint, int>();
        foreach (var (id, value) in Sum(equipped, mods))
            if (stats.Key(id) is uint key) result[key] = result.GetValueOrDefault(key) + value;
        return result;
    }

    /// <summary>One item's local stats by Stats.dat Id (local_critical_strike_chance_+% …).</summary>
    public static Dictionary<string, int> Local(Item item, ModCatalog mods) => Sum([item], mods, local: true);

    /// <summary>A worn item with what it needs: strength, dexterity, intelligence and character level.</summary>
    public sealed record Worn(uint Id, Item Item, int Strength, int Dexterity, int Intelligence, int Level);

    /// <summary>
    /// The worn items that are inactive (their requirements are not met), as in the game: an item stays equipped but its
    /// mods and socketed gems do nothing. It works step by step from <paramref name="previous"/> (the inactive items
    /// before this change; ids no longer worn are dropped):
    /// <list type="number">
    /// <item>An inactive item comes back when the character (<paramref name="baseAttributes"/>: class and passives), the
    /// active items and the level meet its requirement; its own attributes do not count while it is off.</item>
    /// <item>Then, with the attributes of every active item counted (its own too: the client only lets an item be put
    /// on when its requirements are met without it, so a worn active item has qualified), one item that falls short is
    /// switched off, the totals are counted again, and so on until every active item is satisfied.</item>
    /// </list>
    /// Taking off a +30 strength ring that another +30 strength item needed leaves that item on if the rest still
    /// reaches its requirement; an item it switches off can switch off more.
    /// </summary>
    public static (HashSet<uint> Inactive, IReadOnlyList<Item> Active) ResolveEquipment(
        IReadOnlyList<Worn> worn, (int Str, int Dex, int Int) baseAttributes, int level, ModCatalog mods,
        IReadOnlySet<uint>? previous = null)
        => ResolveEquipment(worn, level, active =>
        {
            var raw = RawAttributes(active.Select(w => w.Item), mods);
            return AttributeModifiers.FromWide(name => raw.GetValueOrDefault(name))
                .Apply(baseAttributes.Str, baseAttributes.Dex, baseAttributes.Int);
        }, previous);

    /// <summary>Resolve the same staged policy with final attributes recalculated from the active raw sources.</summary>
    public static (HashSet<uint> Inactive, IReadOnlyList<Item> Active) ResolveEquipment(
        IReadOnlyList<Worn> worn, int level, Func<IReadOnlyList<Worn>, (int Str, int Dex, int Int)> finalAttributes,
        IReadOnlySet<uint>? previous = null)
    {
        var inactive = Inactive(worn, level, finalAttributes, previous);
        return (inactive, worn.Where(w => !inactive.Contains(w.Id)).Select(w => w.Item).ToArray());
    }

    public static HashSet<uint> Inactive(IReadOnlyList<Worn> worn, (int Str, int Dex, int Int) baseAttributes, int level,
        Func<Item, (int Str, int Dex, int Int)> attributesOf, IReadOnlySet<uint>? previous = null)
    {
        var own = worn.ToDictionary(w => w.Id, w => attributesOf(w.Item));
        return Inactive(worn, level, active => new AttributeModifiers(
            active.Sum(w => (long)own[w.Id].Str),
            active.Sum(w => (long)own[w.Id].Dex),
            active.Sum(w => (long)own[w.Id].Int))
                .Apply(baseAttributes.Str, baseAttributes.Dex, baseAttributes.Int), previous);
    }

    public static HashSet<uint> Inactive(IReadOnlyList<Worn> worn, int level,
        Func<IReadOnlyList<Worn>, (int Str, int Dex, int Int)> finalAttributes, IReadOnlySet<uint>? previous = null)
    {
        var inactive = worn.Where(w => previous?.Contains(w.Id) == true).Select(w => w.Id).ToHashSet();
        (int, int, int) Totals() => finalAttributes(worn.Where(w => !inactive.Contains(w.Id)).ToArray());
        bool Meets(Worn w, (int Str, int Dex, int Int) t) => level >= w.Level && t.Str >= w.Strength && t.Dex >= w.Dexterity && t.Int >= w.Intelligence;
        // 1. Back on: met by the others (an inactive item is not in the totals).
        for (bool changed = true; changed;)
        {
            changed = false;
            if (worn.FirstOrDefault(w => inactive.Contains(w.Id) && Meets(w, Totals())) is { } back) { inactive.Remove(back.Id); changed = true; }
        }
        // 2. Off, one at a time, in equipment order, recounting after each.
        for (bool changed = true; changed;)
        {
            changed = false;
            var totals = Totals();
            if (worn.FirstOrDefault(w => !inactive.Contains(w.Id) && !Meets(w, totals)) is { } off) { inactive.Add(off.Id); changed = true; }
        }
        return inactive;
    }

    /// <summary>The attributes an item's global mods give (additional_strength, all attributes, the pairs …).</summary>
    public static (int Str, int Dex, int Int) Attributes(Item item, ModCatalog mods)
    {
        var raw = RawAttributes([item], mods);
        return AttributeModifiers.FromWide(name => raw.GetValueOrDefault(name)).Flat;
    }

    /// <summary>
    /// An item's character level requirement, as the client computes it (0x1421e03d0, stored in the Mods component
    /// +0x2bc; nothing is sent): a local_level_requirement_override wins outright; otherwise the item type's level, raised
    /// to Mods.dat Level × 4 / 5 of every mod in all five lists, plus local_level_requirement_+, at least 0. The client
    /// shows it in the tooltip but does not refuse an equip below it, so the server does.
    /// </summary>
    public static int LevelRequirement(Item item, int dropLevel, ModCatalog mods)
    {
        var local = Sum([item], mods, local: true);
        if (local.GetValueOrDefault("local_level_requirement_override") is int over and not 0) return Math.Max(0, over);
        int required = Math.Max(dropLevel, item.Mods.Select(m => (mods.ByHash(m.Hash)?.Level ?? 0) * 4 / 5).DefaultIfEmpty(0).Max());
        return Math.Max(0, required + local.GetValueOrDefault("local_level_requirement_+"));
    }

    /// <summary>Two stat dictionaries added together (passives + items).</summary>
    public static Dictionary<uint, int> Plus(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        var sum = new Dictionary<uint, int>(a);
        foreach (var (k, v) in b) sum[k] = ArcaneMight.Combine(k, sum.GetValueOrDefault(k), v);
        return sum;
    }
}
