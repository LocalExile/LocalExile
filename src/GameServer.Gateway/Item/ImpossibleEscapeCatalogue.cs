using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Impossible Escape (Viridian Jewel, Small, limited to 1): a generated unique whose variant is one of the client's
/// keystones. The client's <c>MavenJewelRadiusKeystones</c> table lists the PassiveSkills.dat rows, and the item's
/// <c>JewelUniqueAllocateDisconnectedPassivesAroundKeystone</c> mod stores the chosen row and the 800 radius.
/// The server uses that value for the allocation bypass around the keystone.
/// </summary>
static class ImpossibleEscapeCatalogue
{
    const string ModId = "JewelUniqueAllocateDisconnectedPassivesAroundKeystone";
    const string KeystoneStat = "local_unique_jewel_disconnected_passives_can_be_allocated_around_keystone_hash";
    const string RadiusStat = "local_jewel_effect_base_radius";
    // Path of Building 3_16's Small radius is 960 (the client's PassiveJewelRadii row is the 800 display/base
    // value). Impossible Escape's keystone ring follows the 3_16 effect radius.
    const int Radius = 960;

    public sealed record Keystone(int Row, ushort GraphId, string Name);

    static readonly Lazy<List<Keystone>> Entries = new(Load);

    public static IReadOnlyList<Keystone> All => Entries.Value;

    public static string[] Names => [.. All.Select(entry => entry.Name)];

    public static Keystone? Find(string query)
    {
        if (int.TryParse(query, out int value))
        {
            if (All.FirstOrDefault(entry => entry.Row == value || entry.GraphId == value) is { } numeric) return numeric;
        }
        return All.FirstOrDefault(entry => entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public static Item? Create(ItemCatalog items, string query)
    {
        if (Find(query) is not { } keystone) return null;
        if (items.Find("Viridian Jewel") is not { } baseType) return null;
        if (items.Mods.ById(ModId) is not { Stats.Count: > 0 } mod) return null;
        if (UniqueJewelVisuals.Of("Impossible Escape") is not ushort visual) return null;
        if (RadiusJewelCatalogue.WordHash("Impossible Escape", "data") is not uint word) return null;
        var values = mod.MaxValues().ToArray();
        // The client stat is a keystone hash, not the MavenJewelRadiusKeystones row: the graph id resolves to the
        // keystone name in the item tooltip and moves the ring from the socket to the keystone.
        values[0] = keystone.GraphId;
        return ItemCatalog.Create(baseType) with
        {
            Rarity = ItemRarity.Unique,
            Corrupted = true,
            Identified = true,
            ItemLevel = 1,
            VisualIdentity = visual,
            NameWords = [word],
            Mods = [new ItemMod(ModSection.Explicit, mod.Hash, values)],
        };
    }

    /// <summary>Legacy items made before the keystone hash fix stored the MavenJewelRadiusKeystones row; rewrite that
    /// value to the PassiveSkillGraphId the client expects. Current items are returned unchanged.</summary>
    public static Item Migrate(Item item, ModCatalog mods)
    {
        if (Read(item, mods) is not { } read) return item;
        if (Program.Passives.Find((ushort)read.Value) is { IsKeystone: true }) return item;
        if (Program.Passives.GraphIdForRow(read.Value) is not ushort graphId) return item;
        var rewritten = new List<ItemMod>(item.Mods.Count);
        foreach (var itemMod in item.Mods)
        {
            if (mods.ByHash(itemMod.Hash) is not { } mod)
            {
                rewritten.Add(itemMod);
                continue;
            }
            int at = -1;
            for (int i = 0; i < mod.Stats.Count; i++)
                if (mod.Stats[i].Id == KeystoneStat) { at = i; break; }
            if (at < 0 || at >= itemMod.Values.Count)
            {
                rewritten.Add(itemMod);
                continue;
            }
            var values = itemMod.Values.ToArray();
            values[at] = graphId;
            rewritten.Add(itemMod with { Values = values });
        }
        return item with { Mods = rewritten };
    }

    /// <summary>The keystone hash stored by the item and the radius around that keystone, or null for another jewel.</summary>
    public static (int Value, int Radius)? Read(Item item, ModCatalog mods)
    {
        int value = 0, radius = Radius;
        bool found = false;
        foreach (var itemMod in item.Mods)
        {
            if (mods.ByHash(itemMod.Hash) is not { } mod) continue;
            for (int i = 0; i < mod.Stats.Count && i < itemMod.Values.Count; i++)
            {
                if (mod.Stats[i].Id == KeystoneStat)
                {
                    value = itemMod.Values[i];
                    found = true;
                }
                else if (mod.Stats[i].Id == RadiusStat)
                {
                    radius = itemMod.Values[i];
                }
            }
        }
        return found ? (value, Math.Max(radius, Radius)) : null;
    }

    static List<Keystone> Load()
    {
        var result = new List<Keystone>();
        string path = Path.Combine("data", "tables", "MavenJewelRadiusKeystones.json");
        if (!File.Exists(path)) return result;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            int row = entry.GetProperty("Keystone").GetInt32();
            ushort? graphId = Program.Passives.GraphIdForRow(row);
            if (graphId is null && Program.Passives.Find((ushort)row) is { IsKeystone: true } direct)
                graphId = direct.GraphId;
            if (graphId is not ushort id || Program.Passives.Find(id) is not { IsKeystone: true } keystone) continue;
            result.Add(new(row, id, keystone.Name));
        }
        return result;
    }
}
