using GameServer.Core.Protocol.Instance;


/// <summary>
/// What a socketed cluster jewel's passives give the character.
///
/// A cluster jewel's small passives all grant its theme ("Added Small Passive Skills grant: ..."), its notables grant the
/// stats of their <c>affliction_notable_*</c> rows, and its jewel sockets grant nothing.
///
/// A jewel in a tree socket is mapped by <see cref="ClusterJewelLayout"/>: its small passives grant the theme as they are
/// allocated, its sockets give nothing, and each notable position applies its own notable's row. A 4-passive medium in an
/// expansion cell is mapped the same way (theme on each small, nothing on its socket). An expansion-cell shape the layout
/// has not measured yet counts once all of its passives are allocated, with the theme on its smalls and every notable's
/// row, and its own socket excluded from the count.
/// </summary>
static class ClusterJewelStats
{
    /// <summary>The theme statistic's target: the character stat each <c>local_affliction_jewel_small_nodes_grant_*</c>
    /// feeds. Stats the server does not read (or has no model for, such as the per-skill-type speed grants) are absent,
    /// so a theme that only has those grants nothing.</summary>
    static readonly Dictionary<string, string> ThemeStats = new(StringComparer.Ordinal)
    {
        ["local_affliction_jewel_small_nodes_grant_maximum_life"] = "base_maximum_life",
        ["local_affliction_jewel_small_nodes_grant_maximum_mana"] = "base_maximum_mana",
        ["local_affliction_jewel_small_nodes_grant_maximum_energy_shield"] = "base_maximum_energy_shield",
        ["local_affliction_jewel_small_nodes_grant_armour"] = "base_physical_damage_reduction_rating",
        ["local_affliction_jewel_small_nodes_grant_evasion"] = "base_evasion_rating",
        ["local_affliction_jewel_small_nodes_grant_str"] = "additional_strength",
        ["local_affliction_jewel_small_nodes_grant_dex"] = "additional_dexterity",
        ["local_affliction_jewel_small_nodes_grant_int"] = "additional_intelligence",
        ["local_affliction_jewel_small_nodes_grant_all_attributes"] = "additional_all_attributes",
        ["local_affliction_jewel_small_nodes_grant_fire_resistance_%"] = "base_fire_damage_resistance_%",
        ["local_affliction_jewel_small_nodes_grant_cold_resistance_%"] = "base_cold_damage_resistance_%",
        ["local_affliction_jewel_small_nodes_grant_lightning_resistance_%"] = "base_lightning_damage_resistance_%",
        ["local_affliction_jewel_small_nodes_grant_chaos_resistance_%"] = "base_chaos_damage_resistance_%",
        ["local_affliction_jewel_small_nodes_grant_elemental_resistance_%"] = "base_resist_all_elements_%",
        ["local_affliction_jewel_small_nodes_grant_damage_+%"] = "damage_+%",
        ["local_affliction_jewel_small_nodes_grant_attack_speed_+%"] = "attack_speed_+%",
        ["local_affliction_jewel_small_nodes_grant_base_cast_speed_+%"] = "base_cast_speed_%",
        ["local_affliction_jewel_small_nodes_grant_mana_regeneration_+%"] = "mana_regeneration_rate_+%",
        ["local_affliction_jewel_small_nodes_grant_%_life_regeneration_per_minute"] = "life_regeneration_rate_per_minute_%",
        ["local_affliction_jewel_small_nodes_grant_base_critical_strike_multiplier_+"] = "base_critical_strike_multiplier_+",
    };

    const string NotablePrefix = "local_affliction_notable_";
    const string KeystonePrefix = "local_jewel_expansion_keystone_";

    /// <summary>The item's mods as (stat id, value) pairs, every section, in item order.</summary>
    public static List<(string Stat, int Value)> Entries(Item jewel)
    {
        var entries = new List<(string Stat, int Value)>();
        foreach (var mod in jewel.Mods)
            if (Program.Items.Mods.ByHash(mod.Hash) is { } row)
                for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
                    entries.Add((row.Stats[i].Id, mod.Values[i]));
        return entries;
    }

    /// <summary>The stats of every socketed cluster jewel's allocated passives.</summary>
    public static IEnumerable<(uint Key, int Value)> For(ClientState conn, IReadOnlyList<InventoryItem> jewels,
        ModCatalog mods, StatCatalog stats)
    {
        if (conn.ClusterPassives.Count == 0) yield break;
        // A tree socket has a mapped block: its small passives give the theme as they are taken, its sockets nothing,
        // and its notables' rows come once every passive is allocated. The ids outside every such block belong to the
        // expansion cells, which have no mapped block and count once their passives are all allocated.
        int known = 0;
        foreach (var item in jewels)
        {
            if (ClusterJewelLayout.For(item.X, item.Item) is not { } layout) continue;
            int inside = 0;
            foreach (ushort id in conn.ClusterPassives)
            {
                if (id < layout.Base || id >= layout.Base + layout.Total) continue;
                inside++;
                int offset = id - layout.Base;
                switch (layout.ByOffset.GetValueOrDefault(offset))
                {
                    case ClusterJewelLayout.Kind.Small:
                        foreach (var stat in Theme(item.Item, 1, mods, stats)) yield return stat;
                        break;
                    case ClusterJewelLayout.Kind.Notable:
                        if (layout.NotableOrders.TryGetValue(offset, out int order))
                            foreach (var stat in Notable(item.Item, order))
                                yield return stat;
                        break;
                }
            }
            known += inside;
        }
        int left = conn.ClusterPassives.Count - known;
        foreach (var item in jewels)
        {
            if (ClusterJewelLayout.For(item.X, item.Item) is not null) continue;
            int taken = Math.Min(left, Game.ClusterNodeBudget(item.Item));
            int sockets = Game.ClusterSocketCount(item.Item);
            int passives = Math.Max(0, Game.ClusterNodeBudget(item.Item) - sockets);
            if (taken >= passives)
            {
                var entries = Entries(item.Item);
                int notables = entries.Count(e => e.Stat.StartsWith(NotablePrefix, StringComparison.Ordinal));
                int keystones = entries.Count(e => e.Stat.StartsWith(KeystonePrefix, StringComparison.Ordinal));
                int smalls = Math.Max(0, passives - notables - keystones);
                foreach (var stat in Theme(item.Item, smalls, mods, stats)) yield return stat;
                for (int order = 0; order < notables; order++)
                    foreach (var stat in Notable(item.Item, order))
                        yield return stat;
            }
            left -= taken;
        }
    }

    /// <summary>The theme for `smalls` small passives of this jewel, scaled by its small-passive effect.</summary>
    static IEnumerable<(uint Key, int Value)> Theme(Item jewel, int smalls, ModCatalog mods, StatCatalog stats)
    {
        if (smalls <= 0) yield break;
        var entries = Entries(jewel);
        int effect = 100 + entries.Where(e => e.Stat == "local_affliction_jewel_small_nodes_have_effect_+%").Sum(e => e.Value);
        foreach (var (stat, value) in entries)
        {
            if (!ThemeStats.TryGetValue(stat, out string? target)) continue;
            if (stats.Key(target) is not uint key) continue;
            yield return (key, (int)Math.Floor(value * smalls * effect / 100.0));
        }
    }

    /// <summary>The jewel's notables in the client's placement order: ascending Stats.dat row of their notable stat
    /// (Path of Building's notableSortOrder). The layout's notable positions take them in this order.</summary>
    public static List<string> Notables(Item jewel) =>
        [.. Entries(jewel).Where(e => e.Stat.StartsWith(NotablePrefix, StringComparison.Ordinal))
            .Select(e => (e.Stat, Key: Program.Stats.Key(e.Stat) ?? uint.MaxValue))
            .OrderBy(x => x.Key)
            .Select(x => x.Stat)];

    /// <summary>The stats of the notable at that position in the jewel's mods.</summary>
    static IEnumerable<(uint Key, int Value)> Notable(Item jewel, int order)
    {
        var notables = Notables(jewel);
        if (order >= notables.Count) yield break;
        string row = "affliction_notable_" + notables[order][NotablePrefix.Length..];
        if (Program.Passives.FindById(row) is not { } node) yield break;
        foreach (var (key, value) in node.Stats) yield return (key, value);
    }
}
