using GameServer.Core.Protocol.Instance;


/// <summary>
/// What a socketed cluster jewel's passives give the character.
///
/// A cluster jewel's small passives all grant its theme ("Added Small Passive Skills grant: ..."): the stats of the
/// passive its enchantment names (<see cref="ClusterJewels.ThemeOf"/>), and on top what its "also grant" affixes add.
/// Its notables grant the stats of their <c>affliction_notable_*</c> rows, and its jewel sockets grant nothing. A jewel
/// made before the server wrote themes names none: its small passives are the client's empty "Nothingness" and grant
/// only the "also grant" mods, wherever on the item those are.
///
/// A jewel in a tree socket is mapped by <see cref="ClusterJewelLayout"/>: its small passives grant the theme as they are
/// allocated, its sockets give nothing, and each notable position applies its own notable's row. A 4-passive medium in an
/// expansion cell is mapped the same way (theme on each small, nothing on its socket). An expansion-cell shape the layout
/// has not measured yet counts once all of its passives are allocated, with the theme on its smalls and every notable's
/// row, and its own socket excluded from the count.
/// </summary>
public static class ClusterJewelStats
{
    /// <summary>The target of an "also grant" mod: the character stat each
    /// <c>local_affliction_jewel_small_nodes_grant_*</c> feeds. Stats the server does not read (or has no model for, such
    /// as the per-skill-type speed grants) are absent, so a mod that only has those grants nothing.</summary>
    static readonly Dictionary<uint, uint> ThemeStats = new()
    {
        [Stat.LocalAfflictionJewelSmallNodesGrantMaximumLife] = Stat.BaseMaximumLife,
        [Stat.LocalAfflictionJewelSmallNodesGrantMaximumMana] = Stat.BaseMaximumMana,
        [Stat.LocalAfflictionJewelSmallNodesGrantMaximumEnergyShield] = Stat.BaseMaximumEnergyShield,
        [Stat.LocalAfflictionJewelSmallNodesGrantArmour] = Stat.BasePhysicalDamageReductionRating,
        [Stat.LocalAfflictionJewelSmallNodesGrantEvasion] = Stat.BaseEvasionRating,
        [Stat.LocalAfflictionJewelSmallNodesGrantStr] = Stat.AdditionalStrength,
        [Stat.LocalAfflictionJewelSmallNodesGrantDex] = Stat.AdditionalDexterity,
        [Stat.LocalAfflictionJewelSmallNodesGrantInt] = Stat.AdditionalIntelligence,
        [Stat.LocalAfflictionJewelSmallNodesGrantAllAttributes] = Stat.AdditionalAllAttributes,
        [Stat.LocalAfflictionJewelSmallNodesGrantFireResistancePct] = Stat.BaseFireDamageResistancePct,
        [Stat.LocalAfflictionJewelSmallNodesGrantColdResistancePct] = Stat.BaseColdDamageResistancePct,
        [Stat.LocalAfflictionJewelSmallNodesGrantLightningResistancePct] = Stat.BaseLightningDamageResistancePct,
        [Stat.LocalAfflictionJewelSmallNodesGrantChaosResistancePct] = Stat.BaseChaosDamageResistancePct,
        [Stat.LocalAfflictionJewelSmallNodesGrantElementalResistancePct] = Stat.BaseResistAllElementsPct,
        [Stat.LocalAfflictionJewelSmallNodesGrantDamagePlusPct] = Stat.DamagePlusPct,
        [Stat.LocalAfflictionJewelSmallNodesGrantAttackSpeedPlusPct] = Stat.AttackSpeedPlusPct,
        [Stat.LocalAfflictionJewelSmallNodesGrantBaseCastSpeedPlusPct] = Stat.Of("base_cast_speed_%"),
        [Stat.LocalAfflictionJewelSmallNodesGrantManaRegenerationPlusPct] = Stat.ManaRegenerationRatePlusPct,
        [Stat.LocalAfflictionJewelSmallNodesGrantPctLifeRegenerationPerMinute] = Stat.LifeRegenerationRatePerMinutePct,
        [Stat.LocalAfflictionJewelSmallNodesGrantBaseCriticalStrikeMultiplierPlus] = Stat.BaseCriticalStrikeMultiplierPlus,
    };

    const string NotablePrefix = "local_affliction_notable_";
    const string KeystonePrefix = "local_jewel_expansion_keystone_";

    // The item's local stat and the PassiveSkills ID sometimes use different names.
    public static PassiveTree.Node? Keystone(Item jewel, PassiveTree? tree = null)
    {
        foreach (var (stat, value) in Entries(jewel))
        {
            string name = Program.Stats.Name(stat);
            if (value == 0 || !name.StartsWith(KeystonePrefix, StringComparison.Ordinal)) continue;
            string suffix = name[KeystonePrefix.Length..] switch
            {
                "hollow_palm_technique" => "dragon_style",
                "natures_patience" => "nature's_patience",
                "veterans_awareness" => "veteran's_awareness",
                var id => id,
            };
            if ((tree ?? Program.Passives).FindById("affliction_keystone_" + suffix) is { IsKeystone: true } node) return node;
        }
        return null;
    }

    /// <summary>The item's mods as (stat id, value) pairs, every section, in item order.</summary>
    public static List<(uint Stat, int Value)> Entries(Item jewel)
    {
        var entries = new List<(uint Stat, int Value)>();
        foreach (var mod in jewel.Mods)
            if (Program.Items.Mods.ByHash(mod.Hash) is { } row)
                for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
                    entries.Add((row.Stats[i].Id, mod.Values[i]));
        return entries;
    }

    /// <summary>The stats of every socketed cluster jewel's allocated passives.</summary>
    public static IEnumerable<(uint Key, int Value)> For(Player player, IReadOnlyList<InventoryItem> jewels,
        ModCatalog mods, StatCatalog stats)
    {
        if (player.Character.ClusterPassives.Count == 0) yield break;
        // A tree socket has a mapped block: its small passives give the theme as they are taken, its sockets nothing,
        // and its notables' rows come once every passive is allocated. The ids outside every such block belong to the
        // expansion cells, which have no mapped block and count once their passives are all allocated.
        int known = 0;
        foreach (var item in jewels)
        {
            if (ClusterJewelLayout.For(item.X, item.Item) is not { } layout) continue;
            int inside = 0;
            foreach (ushort id in player.Character.ClusterPassives)
            {
                if (id < layout.Base || id >= layout.Base + layout.Total) continue;
                inside++;
                int offset = id - layout.Base;
                switch (layout.ByOffset.GetValueOrDefault(offset))
                {
                    case ClusterJewelLayout.Kind.Keystone:
                        if (Keystone(item.Item, Game.PassivesFor(player)) is { } keystone)
                            foreach (var stat in keystone.Stats) yield return stat;
                        break;
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
        int left = player.Character.ClusterPassives.Count - known;
        foreach (var item in jewels)
        {
            if (ClusterJewelLayout.For(item.X, item.Item) is not null) continue;
            int taken = Math.Min(left, Game.ClusterNodeBudget(item.Item));
            int sockets = Game.ClusterSocketCount(item.Item);
            int passives = Math.Max(0, Game.ClusterNodeBudget(item.Item) - sockets);
            if (taken >= passives)
            {
                var entries = Entries(item.Item);
                int notables = entries.Count(e => Program.Stats.Name(e.Stat).StartsWith(NotablePrefix, StringComparison.Ordinal));
                int keystones = entries.Count(e => Program.Stats.Name(e.Stat).StartsWith(KeystonePrefix, StringComparison.Ordinal));
                int smalls = Math.Max(0, passives - notables - keystones);
                foreach (var stat in Theme(item.Item, smalls, mods, stats)) yield return stat;
                for (int order = 0; order < notables; order++)
                    foreach (var stat in Notable(item.Item, order))
                        yield return stat;
            }
            left -= taken;
        }
    }

    /// <summary>What `smalls` small passives of this jewel grant: its theme's own stats and its "also grant" mods, both
    /// scaled by its small-passive effect.</summary>
    static IEnumerable<(uint Key, int Value)> Theme(Item jewel, int smalls, ModCatalog mods, StatCatalog stats)
    {
        if (smalls <= 0) yield break;
        var entries = Entries(jewel);
        int effect = 100 + entries.Where(e => e.Stat == Stat.LocalAfflictionJewelSmallNodesHaveEffectPlusPct).Sum(e => e.Value);
        if (Program.Items.ByHash(jewel.TypeHash) is { } type && ClusterJewels.ThemeOf(type, jewel) is { } theme
            && Program.Passives.FindById(theme.Passive) is { } passive)
            foreach (var (key, value) in passive.Stats)
                yield return (key, (int)Math.Floor(value * smalls * effect / 100.0));
        foreach (var (stat, value) in entries)
        {
            if (!ThemeStats.TryGetValue(stat, out uint key)) continue;
            yield return (key, (int)Math.Floor(value * smalls * effect / 100.0));
        }
    }

    /// <summary>The jewel's notables in the client's placement order: ascending Stats.dat row of their notable stat
    /// (Path of Building's notableSortOrder). The layout's notable positions take them in this order.</summary>
    public static List<uint> Notables(Item jewel) =>
        [.. Entries(jewel).Select(e => e.Stat)
            .Where(s => Program.Stats.Name(s).StartsWith(NotablePrefix, StringComparison.Ordinal)).Order()];

    /// <summary>The stats of the notable at that position in the jewel's mods.</summary>
    static IEnumerable<(uint Key, int Value)> Notable(Item jewel, int order)
    {
        var notables = Notables(jewel);
        if (order >= notables.Count) yield break;
        string suffix = Program.Stats.Name(notables[order])[NotablePrefix.Length..] switch
        {
            "arcane_focus" => "arcane_adept",
            "summer_commander" => "fiery_commander",
            "winter_commander" => "icy_commander",
            "grounded_commander" => "shocking_commander",
            var id => id,
        };
        string row = "affliction_notable_" + suffix;
        if (Program.Passives.FindById(row) is not { } node) yield break;
        foreach (var (key, value) in node.Stats) yield return (key, value);
    }
}
