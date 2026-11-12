using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// The seven Forbidden ascendancy tattoos of issue #372: each replaces an ascendancy notable with a random
// non-multiple-choice notable of its class (PassiveSkillOverrides Type 17-23 pools); the pick is saved in
// TattooKeystones and replaces the node's stats, and 0x33 sends the chosen notable's HASH16.
public sealed class TattooForbiddenTests
{
    static readonly string[] Names =
    [
        "Forbidden Tattoo of the Duelist", "Forbidden Tattoo of the Marauder", "Forbidden Tattoo of the Ranger",
        "Forbidden Tattoo of the Scion", "Forbidden Tattoo of the Shadow", "Forbidden Tattoo of the Templar",
        "Forbidden Tattoo of the Witch",
    ];

    [Fact]
    public void Every_forbidden_tattoo_has_a_pool_of_its_class_ascendancy_notables()
    {
        var conn = new ClientState(new(1, "tattoo-forbidden", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TattooForbiddenChar", PlayerLife = 1000,
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        var tree = Game.PassivesFor(conn);
        foreach (string name in Names)
        {
            var entry = TattooCatalogue.Find(name);
            Assert.NotNull(entry);
            Assert.True(entry!.Forbidden);
            Assert.Equal(40, entry.Limit);
            Assert.Equal("Ascendancy Notable", entry.Target);
            var pool = ForbiddenTattoo.Pool(name);
            Assert.True(pool is not null, name);
            Assert.True(pool!.Count > 1, name);
            foreach (ushort notable in pool)
                Assert.True(tree.Find(notable) is { IsAscendancy: true }, $"{name}: {notable}");
        }
        // The client's Shadow pool includes the two option-labelled notables (Knife in the Back, For the Jugular).
        Assert.Equal(26, ForbiddenTattoo.Pool("Forbidden Tattoo of the Shadow")!.Count);
    }

    [Fact]
    public void A_forbidden_pick_is_another_notable_of_the_class_and_supplies_its_stats()
    {
        Assert.True(ForbiddenTattoo.IsForbidden("Forbidden Tattoo of the Witch"));
        Assert.False(ForbiddenTattoo.IsForbidden("Tattoo of the Kitava Heart Eater"));
        Assert.Null(ForbiddenTattoo.Pool("Tattoo of the Kitava Heart Eater"));

        var conn = new ClientState(new(1, "tattoo-forbidden", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TattooForbiddenChar", PlayerLife = 1000,
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        var tree = Game.PassivesFor(conn);
        ushort[] witchPool = [.. ForbiddenTattoo.Pool("Forbidden Tattoo of the Witch")!];
        // Not the replaced node and none of the character's other allocated notables: a rolled duplicate would count
        // its stats twice.
        ushort[] allocated = [27096, 37127, 31344];
        for (int i = 0; i < 20; i++)
        {
            ushort picked = ForbiddenTattoo.Pick("Forbidden Tattoo of the Witch", 27096, allocated)!.Value;
            Assert.Contains(picked, witchPool);
            Assert.DoesNotContain(picked, allocated);
            var stats = ForbiddenTattoo.Stats(tree, picked);
            Assert.NotNull(stats);
            Assert.NotEmpty(stats!);
        }
        Assert.Null(ForbiddenTattoo.Pick("Forbidden Tattoo of the Witch", 0, witchPool));
        Assert.Null(ForbiddenTattoo.Stats(tree, null));
        // TattooBlockingPassive: the Duelist's Overwhelm row (hash 17315) blocks Sightless (passive row 5471, graph
        // id 24698) from being replaced, and the pick never offers it for that node.
        Assert.True(ForbiddenTattoo.Blocks(17315, 24698));
        Assert.False(ForbiddenTattoo.Blocks(17315, 27096));
    }
}
