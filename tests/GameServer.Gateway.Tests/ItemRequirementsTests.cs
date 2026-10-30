using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemRequirementsTests
{
    // Items carry their attribute bonus in a lookup by id (the real code reads the item's mods).
    static readonly Dictionary<uint, (int, int, int)> Gives = [];

    static ItemModStats.Worn Item(uint id, (int Str, int Dex, int Int) needs, (int Str, int Dex, int Int) gives = default, int level = 0)
    {
        Gives[id] = gives;
        return new(id, new Item(id, ["Base"], 0), needs.Str, needs.Dex, needs.Int, level);
    }

    static HashSet<uint> Inactive((int, int, int) baseAttributes, int level, params ItemModStats.Worn[] worn) =>
        Inactive(baseAttributes, level, null, worn);

    static HashSet<uint> Inactive((int, int, int) baseAttributes, int level, HashSet<uint>? previous, params ItemModStats.Worn[] worn) =>
        ItemModStats.Inactive(worn, baseAttributes, level, i => Gives[i.TypeHash], previous);

    [Fact]
    public void Taking_off_a_ring_leaves_items_the_rest_still_supports()
    {
        // Live 2026-10-20: 14 strength, a +30 ring, a +30 Chestplate (needs 25), a Cone Helmet (needs 21): 74.
        // Without the ring 44 is left, enough for both.
        var chest = Item(1, (25, 0, 0), gives: (30, 0, 0));
        var helmet = Item(2, (21, 0, 0));
        Assert.Empty(Inactive((14, 0, 0), 10, chest, helmet));
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
