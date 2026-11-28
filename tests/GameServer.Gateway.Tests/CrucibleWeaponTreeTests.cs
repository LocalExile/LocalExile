using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>A Crucible tree travels inside the Mods component (flag bit 7) and its allocated nodes also ride in the
/// hidden list, which is the only list the stat calculation reads. This check covers the save round trip and that the
/// nodes reach the item's stats; the client-side layout is in Agents/docs/protocol/13-items.md.</summary>
public class CrucibleWeaponTreeTests
{
    static (Item Item, WeaponTree Tree) WithTree()
    {
        var items = Program.Items;
        var held = new ItemMod(ModSection.Implicit, items.Mods.ById("IncreasedLife1")!.Hash, [10]);
        var tree = new WeaponTree
        {
            BalanceRow = 84, Progress = 956484, NodeMask = 1, AllocatedMask = 1,
            TierPicks = [0, 0xff, 0xff, 0xff, 0xff], Nodes = [held],
        };
        return (ItemCatalog.Create(items.Find("Driftwood Sceptre")!) with { WeaponTree = tree }, tree);
    }

    [Fact]
    public void A_tree_survives_a_save_and_its_allocated_nodes_reach_the_item_stats()
    {
        // The Mods component's record round trips, node values and masks included.
        var (item, tree) = WithTree();
        var saved = CharacterSaves.ToSaved(item, Program.Items.Mods);
        Assert.NotNull(saved.WeaponTree);

        var back = CharacterSaves.FromSaved(saved, Program.Items);
        var got = Assert.IsType<WeaponTree>(back.WeaponTree);
        Assert.Equal(tree.BalanceRow, got.BalanceRow);
        Assert.Equal(tree.Progress, got.Progress);
        Assert.Equal(tree.NodeMask, got.NodeMask);
        Assert.Equal(tree.AllocatedMask, got.AllocatedMask);
        Assert.Equal(tree.TierPicks, got.TierPicks);
        Assert.Equal(tree.Nodes[0].Hash, Assert.Single(got.Nodes).Hash);
        Assert.Equal(tree.Nodes[0].Values, Assert.Single(got.Nodes).Values);
        Assert.Equal(ModSection.Hidden, Assert.Single(got.AllocatedMods).Section);

        // An allocated node reaches the stats, and only while it is allocated: the tree record itself never does.
        var plain = ItemCatalog.Create(Program.Items.Find("Driftwood Sceptre")!);
        var plainStats = ItemModStats.Values(plain, Program.Items.Mods).ToList();
        var treeStats = ItemModStats.Values(item, Program.Items.Mods).ToList();
        Assert.Contains(treeStats, stat => stat.Value == 10);
        Assert.True(treeStats.Count > plainStats.Count);

        Assert.Equal(plainStats.Count,
            ItemModStats.Values(item with { WeaponTree = tree with { AllocatedMask = 0 } }, Program.Items.Mods).Count());
    }
}
