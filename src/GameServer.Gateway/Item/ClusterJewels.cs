using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The cluster jewel as an item (Agents/docs/game/cluster-jewels.md): what its enchantments hold, read from the client's
/// tables, and how a new one is made.
///
/// A cluster jewel is its base plus two enchantments. "Adds N Passive Skills" (<c>JewelExpansionPassiveNodes</c>) has
/// two values: the theme, a <c>PassiveTreeExpansionSkills</c> row plus one (0 names none), and the passive count, within
/// <c>PassiveTreeExpansionJewels</c>' MinNodes and MaxNodes for the base. The client words the theme itself ("Added
/// Small Passive Skills grant: 12% increased Fire Damage") and builds the small passives and the mastery from that row;
/// with 0 every small passive is the empty "Nothingness". A large or medium jewel also says how many of its passives
/// are jewel sockets. Everything else is an affix: notables, "also grant" and the small passives' effect.
/// </summary>
public static class ClusterJewels
{
    public const string PassivesMod = "JewelExpansionPassiveNodes";
    public const string ThemeStat = "local_jewel_expansion_passive_node_index";
    public const string CountStat = "local_jewel_expansion_passive_node_count";

    /// <summary>What a cluster jewel's small passives are: a <c>PassiveTreeExpansionSkills</c> row.</summary>
    /// <param name="Size">0 small, 1 medium, 2 large: the jewels the theme is for.</param>
    /// <param name="Tag">The spawn tag it gives the jewel: the notables that can roll with this theme carry it.</param>
    /// <param name="Passive">The PassiveSkills Id of the small passive.</param>
    public sealed record Theme(int Row, int Size, string Tag, string Passive, string Name)
    {
        /// <summary>The value the enchantment holds for this theme.</summary>
        public int Value => Row + 1;

        /// <summary>A row the game no longer gives a new jewel (its tag says so).</summary>
        public bool Retired => Tag.StartsWith("old_do_not_use", StringComparison.Ordinal);
    }

    /// <summary>A cluster jewel base: its size, how many passives it can add, its enchantment mods and its themes.</summary>
    /// <param name="Sockets">The jewel-socket enchantments of the size by their socket count: none for a small one.</param>
    public sealed record Base(int Size, int MinNodes, int MaxNodes, ModCatalog.Mod Passives,
        IReadOnlyDictionary<int, ModCatalog.Mod> Sockets, IReadOnlyList<Theme> Themes)
    {
        /// <summary>The jewel sockets a new jewel of the size has: two on a large one, one on a medium one.</summary>
        public int DefaultSockets => Sockets.Count == 0 ? 0 : Sockets.Keys.Max();

        /// <summary>The enchantments of a jewel with this theme (null for none), passive count and socket count, as the
        /// game orders them. A count outside the base's range is taken as given: only a tool asks for one.</summary>
        public IEnumerable<ItemMod> Enchants(Theme? theme, int passives, int sockets)
        {
            yield return new ItemMod(ModSection.Enchant, Passives.Hash,
                [.. Passives.Stats.Select(s => s.Id == ThemeStat ? theme?.Value ?? 0 : s.Id == CountStat ? passives : 0)]);
            if (Sockets.Count > 0 && sockets > 0 && Sockets.GetValueOrDefault(Math.Min(sockets, DefaultSockets)) is { } socket)
                yield return new ItemMod(ModSection.Enchant, socket.Hash, [.. socket.MaxValues()]);
        }

        /// <summary>The enchantments of a new jewel: one of the size's themes, a passive count in its range and the
        /// size's sockets. The game's own weights are not known: each theme and count is as likely as another.</summary>
        public IEnumerable<ItemMod> Roll(Random random)
        {
            var themes = Themes.Where(t => !t.Retired).ToList();
            return Enchants(themes.Count > 0 ? themes[random.Next(themes.Count)] : null, random.Next(MinNodes, MaxNodes + 1), DefaultSockets);
        }
    }

    /// <summary>Every theme, by its row.</summary>
    public static IReadOnlyList<Theme> Themes => AllThemes.Value;

    static readonly Lazy<IReadOnlyList<Theme>> AllThemes = new(() =>
    {
        if (ClientTables.TableOrNull("PassiveTreeExpansionSkills") is not { } table
            || ClientTables.TableOrNull("PassiveSkills") is not { } passives || ClientTables.TableOrNull("Tags") is not { } tags) return [];
        var themes = new List<Theme>();
        foreach (var r in table)
        {
            if (passives.Row(r.Int("PassiveSkillsKey")) is not { } passive) continue;
            themes.Add(new(r.Index, r.Int("PassiveTreeExpansionJewelSizesKey"),
                r.IntOrNull("TagsKey") is int tag && tags.Row(tag) is { } t ? t.Text("Id") ?? "" : "",
                passive.Text("Id") ?? "", passive.Text("Name") ?? ""));
        }
        return themes;
    });

    /// <summary>The cluster jewel bases by their BaseItemTypes row, for the item catalog as it loads.</summary>
    internal static Dictionary<int, Base> Load(ModCatalog mods)
    {
        var bases = new Dictionary<int, Base>();
        if (ClientTables.TableOrNull("PassiveTreeExpansionJewels") is not { } table || mods.ById(PassivesMod) is not { } passives) return bases;
        foreach (var r in table)
        {
            int size = r.Int("PassiveTreeExpansionJewelSizesKey");
            // The socket enchantments name their size; a large jewel has one for a single socket too.
            var sockets = new Dictionary<int, ModCatalog.Mod>();
            foreach (string id in size switch
            {
                2 => (string[])["JewelExpansionJewelNodesLarge1", "JewelExpansionJewelNodesLarge2___"],
                1 => ["JewelExpansionJewelNodesMedium"],
                _ => [],
            })
                if (mods.ById(id) is { Stats.Count: > 0 } socket) sockets[socket.MaxValues()[0]] = socket;
            bases[r.Int("BaseItemTypesKey")] = new(size, r.Int("MinNodes"), r.Int("MaxNodes"), passives, sockets,
                [.. Themes.Where(t => t.Size == size)]);
        }
        return bases;
    }

    /// <summary>The theme a jewel's enchantment names, or null: no cluster jewel, no enchantment, or the value 0 of the
    /// jewels made before the server wrote themes.</summary>
    public static Theme? ThemeOf(ItemCatalog.BaseType type, Item item)
    {
        if (type.Cluster is not { } cluster) return null;
        foreach (var mod in item.Mods)
        {
            if (mod.Hash != cluster.Passives.Hash) continue;
            for (int i = 0; i < cluster.Passives.Stats.Count && i < mod.Values.Count; i++)
                if (cluster.Passives.Stats[i].Id == ThemeStat)
                    return mod.Values[i] >= 1 && mod.Values[i] <= Themes.Count ? Themes[mod.Values[i] - 1] : null;
        }
        return null;
    }

    /// <summary>A theme by its tag, its passive's Id or its name ("Fire Damage"), whatever the case; of this base's size
    /// first, since two sizes can share a name.</summary>
    public static Theme? FindTheme(Base cluster, string name) =>
        cluster.Themes.Concat(Themes).FirstOrDefault(t => t.Tag.Equals(name, StringComparison.OrdinalIgnoreCase)
            || t.Passive.Equals(name, StringComparison.OrdinalIgnoreCase) || t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A cluster jewel with this theme, passive count, sockets and affixes (notables, "also grant" and the like, each at
    /// its highest values), or null when the base is no cluster jewel. Its rarity follows the affixes as an item's does:
    /// none normal, up to two magic, more rare.
    /// </summary>
    public static Item? Create(ItemCatalog.BaseType type, Theme? theme, int passives, int sockets,
        IEnumerable<ModCatalog.Mod>? affixes = null, int itemLevel = 84)
    {
        if (type.Cluster is not { } cluster) return null;
        var explicits = (affixes ?? []).Select(m => new ItemMod(ModSection.Explicit, m.Hash, [.. m.MaxValues()])).ToList();
        return ItemCatalog.Create(type) with
        {
            ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100),
            Rarity = explicits.Count switch { 0 => ItemRarity.Normal, <= 2 => ItemRarity.Magic, _ => ItemRarity.Rare },
            Mods = [.. cluster.Enchants(theme, passives, sockets), .. explicits],
        };
    }
}
