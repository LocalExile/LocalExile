using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class CommandsRules
{
    /// <summary>A non-unique cluster jewel as a drop rolls it: the size's base type with the enchantments that carry
    /// how many passives it has, what its small passives grant (its theme) and its jewel sockets (two or one for a
    /// large, one for a medium, none for a small). Null when the base type or a mod is missing from the game data.</summary>
    internal static Item? CreateClusterJewel(int size, int passives, int sockets, string? theme = null,
        IEnumerable<string>? notables = null)
    {
        string name = size switch { 2 => "Large Cluster Jewel", 1 => "Medium Cluster Jewel", 0 => "Small Cluster Jewel", _ => "" };
        if (name.Length == 0 || Items.Find(name) is not { } baseType) return null;
        var asked = notables?.ToList() ?? [];
        var mods = new List<ItemMod>();
        // "Adds N Passive Skills": one enchantment mod whose two stats are the node index and the count.
        if (Items.Mods.ById("JewelExpansionPassiveNodes") is { } count && count.Stats.Count >= 2)
            mods.Add(new ItemMod(ModSection.Enchant, count.Hash, [0, passives]));
        string socketMod = size switch
        {
            2 => sockets >= 2 ? "JewelExpansionJewelNodesLarge2___" : "JewelExpansionJewelNodesLarge1",
            1 => "JewelExpansionJewelNodesMedium",
            _ => "",
        };
        if (sockets > 0 && socketMod.Length > 0 && Items.Mods.ById(socketMod) is { } socket && socket.Stats.Count > 0)
            mods.Add(new ItemMod(ModSection.Enchant, socket.Hash, [.. socket.MaxValues()]));
        // The theme: what the cluster's small passives grant ("Added Small Passive Skills grant: ..."). It is an
        // enchantment (like the count and the sockets); a smaller jewel in a larger socket is padded by the client
        // with empty nodes instead, see JewelFitsCell.
        if (Items.Mods.ById(theme ?? "AfflictionJewelSmallPassivesGrantLife_") is { } themed && themed.Stats.Count > 0)
            mods.Add(new ItemMod(ModSection.Enchant, themed.Hash, [.. themed.MaxValues()]));
        // "1 Added Passive Skill is <notable>": an explicit prefix, one per notable the jewel has.
        foreach (string notable in asked)
            if (Items.Mods.ById(notable) is { } found && found.Stats.Count > 0)
                mods.Add(new ItemMod(ModSection.Explicit, found.Hash, [.. found.MaxValues()]));
        return ItemCatalog.Create(baseType) with
        {
            // The notables are the item's only affixes. A jewel without one is Normal, one or two are Magic (a magic
            // item may not have zero affixes: the client then renders the whole cluster as empty "Nothingness" nodes)
            // and three (a large cluster can carry three) Rare.
            Rarity = asked.Count switch { 0 => ItemRarity.Normal, <= 2 => ItemRarity.Magic, _ => ItemRarity.Rare },
            ItemLevel = 68,
            Mods = mods,
        };
    }

    /// <summary>Voices, the unique Large Cluster Jewel: three jewel sockets plus 1, 3, 5 or 7 empty passives (the four
    /// variants, the rarer the fewer). Null when the base type, the unique art or a mod is missing from the game data.
    /// The art is item visual <c>UniqueJewel147</c> (Art/2DItems/Jewels/UniqueJewelBase3.dds, the same row
    /// UniqueStashLayout gives Voices); without it the client draws the plain Large Cluster Jewel.</summary>
    internal static Item? CreateVoices(int emptyPassives)
    {
        string? variant = emptyPassives switch
        {
            1 => "ExpansionJewelEmptyPassiveUnique__1",
            3 => "ExpansionJewelEmptyPassiveUnique__2",
            5 => "ExpansionJewelEmptyPassiveUnique_3_",
            7 => "ExpansionJewelEmptyPassiveUnique__4",
            _ => null,
        };
        if (variant is null || Items.Find("Large Cluster Jewel") is not { } baseType) return null;
        if (UniqueJewelVisuals.Of("Voices") is not ushort visual) return null;
        if (Items.Mods.ById("ExpansionJewel3JewelSockets") is not { } sockets) return null;
        if (Items.Mods.ById(variant) is not { } empty || empty.Stats.Count == 0) return null;
        return ItemCatalog.Create(baseType) with
        {
            Rarity = ItemRarity.Unique,
            Corrupted = true,
            ItemLevel = 1,
            VisualIdentity = visual,
            Mods =
            [
                new ItemMod(ModSection.Explicit, sockets.Hash, [.. sockets.MaxValues()]),
                new ItemMod(ModSection.Explicit, empty.Hash, [.. empty.MaxValues()]),
            ],
        };
    }

    /// <summary>A unique jewel from the global (non-radius) catalogue, max-rolled for predictable testing. Null when the
    /// name is not in the catalogue or a mod cannot be resolved from the current game data.</summary>
    internal static Item? CreateUniqueJewel(string name)
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
