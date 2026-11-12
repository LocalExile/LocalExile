using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class InspiredLearningTests
{
    [Fact]
    public void The_allocated_socket_needs_four_notables_within_its_own_radius()
    {
        var tree = Program.Passives;
        var items = Program.Items;
        var jewel = new UniqueCatalog(items).Create("Inspired Learning", new Random(13))!;
        var radius = ItemModStats.Local(jewel, items.Mods)["local_jewel_effect_base_radius"];
        var eligible = Enumerable.Range(0, 60).Select(tree.JewelNodeForSlot).OfType<ushort>()
            .Select(s => (Socket: s, Notables: tree.NodesInRadius(s, radius).Where(n => tree.Find(n) is
                { IsNotable: true, IsAscendancy: false, IsKeystone: false }).Take(4).ToArray()))
            .First(x => x.Notables.Length == 4);
        var nodes = eligible.Notables.ToHashSet();
        int Granted() => InspiredLearning.Granted(jewel, items.Mods, tree, eligible.Socket, nodes);
        Assert.Equal(0, Granted());
        nodes.Add(eligible.Socket);
        Assert.Equal(1, Granted());
        nodes.Remove(eligible.Notables[0]);
        Assert.Equal(0, Granted());
    }

    [Fact]
    public void The_selected_rare_affix_expires_after_twenty_seconds_without_shortening_Headhunter()
    {
        var mods = Program.Items.Mods;
        var eligible = mods.All.Where(m => m.Domain == 3 && m.Stats.Count > 0 && m.GenerationType == 1)
            .Where(m => m.Name is not ("Fractured" or "Inner Treasure" or "Soul Conduit")).Take(2)
            .Select(m => m.Roll(ModSection.Explicit, new Random(3))).ToArray();
        Assert.Equal(2, eligible.Length);
        var excluded = mods.ById("MonsterModSoulConduit")!.Roll(ModSection.Explicit, new Random(3));
        var set = MonsterModSet.None with { Mods = [.. eligible, excluded] };
        var stolen = new StolenMods();
        stolen.StealRandom(set, mods, 1, 10, new Random(11));
        Assert.Equal(1, stolen.Count);
        Assert.Equal(20, stolen.Step(10, false, out var beltBuff, out var jewelBuff)!.Value.Remaining);
        Assert.Equal(0, beltBuff.Count);
        Assert.Equal(1, jewelBuff.Count);
        Assert.Equal(0, stolen.Step(30, false)!.Value.Stacks);
        stolen.StealRandom(set, mods, 3, 30, new Random(11));
        Assert.Equal(2, stolen.Count);
        stolen.Steal(set with { Mods = eligible }, mods, 35);
        stolen.StealRandom(set, mods, 2, 36, new Random(11));
        Assert.Equal((2, 39d), stolen.Step(56, false)!.Value);
        Assert.Equal(0, stolen.Step(95, false)!.Value.Stacks);
    }
}
