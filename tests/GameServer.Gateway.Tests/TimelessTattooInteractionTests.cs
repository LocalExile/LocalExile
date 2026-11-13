using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// A timeless jewel that replaces a tattooed node disables the tattoo (tattoos.md, "Interactions"). When the tattoo
// was a random one (Journey Makanui here), the passive it granted is no longer granted, so that passive's own node
// must keep its stats instead of being zeroed as a duplicate.
public sealed class TimelessTattooInteractionTests
{
    [Fact]
    public void A_tattooed_node_a_timeless_jewel_replaced_keeps_the_granted_keystone()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-timeless-tattoo-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var conn = new ClientState(new(1, "timeless-tattoo", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TimelessTattooChar", PlayerLife = 1000,
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var tree = Program.Passives;
            var items = Program.Items;
            // A timeless jewel can take a tattooed node only when the tattoo is on an ordinary keystone (the timeless
            // engine rejects ascendancy nodes, so the Forbidden tattoos are never affected) and Makanui is the random
            // tattoo that targets keystones. Brutal Restraint's item roll 0 puts its seed 6630 jewel in the ordinary
            // jewel socket 36634 (slot 1), and its alternate tree replaces the keystone at 34098 ("Mind Over Matter")
            // inside its radius.
            const int jewelSlot = 1;
            const ushort jewelSocket = 36634;
            const ushort replaced = 34098;
            Assert.Equal(jewelSocket, tree.JewelNodeForSlot(jewelSlot));
            Assert.Equal("Mind Over Matter", tree.Find(replaced)?.Name);
            var timelessJewel = Assert.IsType<Item>(new UniqueCatalog(items).Create("Brutal Restraint", new Random(0)));
            var timelessSpec = Assert.IsType<TimelessSpec>(TimelessJewelCatalog.Read(timelessJewel, items.Mods));
            // The granted keystone is any other keystone outside the jewel's radius, so the jewel does not touch it.
            var inRadius = tree.NodesInRadius(jewelSocket, timelessSpec.Radius).ToHashSet();
            Assert.Contains(replaced, inRadius);
            var alternate = Assert.IsType<TimelessJewelEngine.AlternateNode>(
                Game.TimelessJewels.Alternate(tree.Find(replaced)!, timelessSpec, replaced));
            Assert.True(alternate.Replaces);
            var chosen = JourneyMakanuiCatalogue.All.First(entry => entry.GraphId != replaced
                && !inRadius.Contains(entry.GraphId) && tree.Find(entry.GraphId) is { Stats.Count: > 0 });
            var stat = tree.Find(chosen.GraphId)!.Stats[0].Key;
            int Stat(params ushort[] nodes) => Game.EquipmentStatSources(conn, [], nodes, 100).Sources.GetValueOrDefault(stat);
            int AlternateStat(params ushort[] nodes) =>
                Game.EquipmentStatSources(conn, [], nodes, 100).Sources.GetValueOrDefault(alternate.Stats[0].Key);
            Game.ItemsOf(conn).Put(PlayerInventories.PassiveJewels, new(300, (byte)jewelSlot, 0, timelessJewel));
            // Control: the granted keystone counts once while no tattoo is involved.
            int own = Stat(jewelSocket, chosen.GraphId);
            Assert.True(own > 0);
            conn.Tattoos[replaced] = "Journey Tattoo of Makanui";
            conn.TattooKeystones[replaced] = chosen.GraphId;
            conn.TattooRevision++;
            // Control: the tattoo really went in. Without the socket allocated the timeless jewel cannot apply, so
            // the replaced keystone shows the granted keystone's stats, not its own.
            Assert.Equal(own, Stat(replaced));
            // The timeless jewel now replaces the tattooed keystone, so the tattoo is gone with its stats and the
            // granted keystone must keep its own stats rather than be zeroed as a duplicate.
            Assert.Equal(own, Stat(jewelSocket, replaced, chosen.GraphId));
            Assert.NotEqual(0, AlternateStat(jewelSocket, replaced, chosen.GraphId));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
