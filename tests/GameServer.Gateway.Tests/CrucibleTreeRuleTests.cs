using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Crucible;

namespace GameServer.Gateway.Tests;

/// <summary>The Crucible tree's rules (Modules/Legacy/Crucible): a generated tree is a run of layers from 1 with no gap,
/// its notable sits on layer 5, a pick fills the layers above it and the allocation is the prefix the client lights.
/// The pool is the base's spawn-weight pool, so a bow's node never lands on a sceptre. The client-side evidence is in
/// Agents/docs/protocol/13-items.md §2g.</summary>
public class CrucibleTreeRuleTests
{
    // One base per shape: a one-handed weapon, a two-handed one, and a bow (whose nodes the others must not take).
    const string Sceptre = "Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/Sceptre1";
    const string TwoHandMace = "Metadata/Items/Weapons/TwoHandWeapons/TwoHandMaces/TwoHandMace1";
    const string Bow = "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1";

    static ItemCatalog.BaseType Base(string id) =>
        Program.Items.Find(id) ?? throw new InvalidOperationException($"the test base {id} is missing");

    static WeaponTree Generate(string baseId, int itemLevel = 100, int seed = 7, params (int Layer, string Node)[] picks) =>
        CrucibleTrees.Generate(Base(baseId), itemLevel, new Random(seed), picks);

    [Fact]
    public void A_tree_is_a_gap_free_prefix_of_real_nodes_from_the_base_pool()
    {
        Assert.NotEmpty(CrucibleTrees.Nodes);

        // One node per layer, 1 to 5, all allocated, and every node is the layer's type with real values.
        var tree = Generate(Sceptre);
        int mask = 0;
        for (int layer = 1; layer <= CrucibleTrees.LastLayer; layer++) mask |= 1 << CrucibleTrees.Slot(layer);

        Assert.Equal(CrucibleTrees.LastLayer, tree.Nodes.Count);
        Assert.Equal(mask, tree.NodeMask);
        Assert.Equal(mask, tree.AllocatedMask);
        Assert.Equal<byte>([0, 0, 0, 0, 0], tree.TierPicks);
        Assert.Equal(100u, tree.BalanceRow);
        Assert.Equal(CrucibleTrees.Progress(100), tree.Progress);

        for (int i = 0; i < tree.Nodes.Count; i++)
        {
            var node = CrucibleTrees.NodeByHash(tree.Nodes[i].Hash);
            Assert.NotNull(node);
            Assert.Equal(CrucibleTrees.TypeOf(i + 1), node.Type);
            Assert.InRange(node.Tier, 1, CrucibleTrees.LastLayer);
            Assert.Equal(node.Values, tree.Nodes[i].Values);
            Assert.Contains(node.Values, value => value != 0);
        }

        // Every base that carries a tree generates one out of its own pool; every weapon class has a pool, and
        // armour and jewellery have none.
        int carried = 0;
        foreach (var type in Program.Items.All)
        {
            if (!CrucibleTrees.CanHaveTree(type)) continue;
            carried++;
            var other = CrucibleTrees.Generate(type, 84, new Random(type.Row));
            Assert.Equal(CrucibleTrees.LastLayer, other.Nodes.Count);
            foreach (var mod in other.Nodes)
                Assert.True(CrucibleTrees.NodeByHash(mod.Hash)!.Fits(type.Tags), $"{type.Id} took {mod.Hash}");
        }
        Assert.True(carried > 400, $"only {carried} bases carry trees");

        string[] classes = ["Claw", "Dagger", "Wand", "One Hand Sword", "Thrusting One Hand Sword", "Two Hand Sword",
            "One Hand Axe", "Two Hand Axe", "One Hand Mace", "Two Hand Mace", "Sceptre", "Bow", "Staff", "Shield"];
        foreach (string itemClass in classes)
        {
            var type = Program.Items.All.First(t => t.Class == itemClass && t.Supported);
            Assert.True(CrucibleTrees.CanHaveTree(type), $"a {itemClass} carries no tree");
        }

        var armour = Program.Items.All.First(t => t.Class == "Body Armour");
        Assert.False(CrucibleTrees.CanHaveTree(armour));
        Assert.False(CrucibleTrees.CanHaveTree(Program.Items.All.First(t => t.Class == "Ring")));
        Assert.Throws<ArgumentException>(() => CrucibleTrees.Generate(armour, 100, new Random(1)));

        // A pick on layer 3 — the "only T3" case the client cannot draw on its own, since a tree starts at layer 1 —
        // gives a three-layer tree whose layers 1 and 2 are real nodes and whose allocation is the whole prefix.
        var pick = CrucibleTrees.NodeById("WeaponTreeChargeDuration3")!;
        Assert.Contains(3, pick.Layers);
        Assert.True(pick.Fits(Base(Sceptre).Tags));

        var three = Generate(Sceptre, 100, 1, (3, pick.Id));
        Assert.Equal(3, three.Nodes.Count);
        Assert.Equal(pick.Hash, three.Nodes[2].Hash);
        Assert.Equal(1 << CrucibleTrees.Slot(1) | 1 << CrucibleTrees.Slot(2) | 1 << CrucibleTrees.Slot(3), three.NodeMask);
        Assert.Equal(three.NodeMask, three.AllocatedMask);
        Assert.Equal<byte>([0, 0, 0, 0xff, 0xff], three.TierPicks);
        Assert.Equal(3, three.AllocatedMods.Count);
        Assert.All(three.AllocatedMods, mod => Assert.Equal(ModSection.Hidden, mod.Section));
        for (int i = 0; i < 2; i++)
        {
            var node = CrucibleTrees.NodeByHash(three.Nodes[i].Hash);
            Assert.NotNull(node);
            Assert.Equal(CrucibleTrees.TypeOf(i + 1), node.Type);
            Assert.Contains(node.Values, value => value != 0);
        }

        // A pick the layer, the pool or the base cannot hold is refused.
        var notable = CrucibleTrees.NodeById("WeaponTreeSkillCleaveReave")!;
        Assert.Equal(5, Assert.Single(notable.Layers));
        Assert.Equal(1, notable.Type);
        Assert.Throws<ArgumentException>(() => Generate(Sceptre, 100, 1, (1, notable.Id)));
        Assert.Throws<ArgumentException>(() => Generate(Sceptre, 100, 1, (2, pick.Id)));
        var bowNode = CrucibleTrees.NodeById("WeaponTreeSkillTornadoShotSplitArrow")!;
        Assert.Throws<ArgumentException>(() => Generate(Sceptre, 100, 1, (5, bowNode.Id)));
        var oneHand = CrucibleTrees.NodeById("WeaponTreeAddedPhysical1")!;
        Assert.Throws<ArgumentException>(() => Generate(TwoHandMace, 100, 1, (1, oneHand.Id)));
        Assert.Throws<ArgumentException>(() => Generate(Sceptre, 100, 1, (3, "NoSuchNode")));

        // The pool follows the base's tags, not the item class: a spell node for caster weapons, a bow node, and a
        // one-hand node, each refused by the others.
        var sceptre = Base(Sceptre).Tags;
        var twoHanded = Base(TwoHandMace).Tags;
        var bow = Base(Bow).Tags;
        Assert.True(CrucibleTrees.NodeById("WeaponTreeSkillGlacialCascadeIceNova")!.Fits(sceptre));
        Assert.False(CrucibleTrees.NodeById("WeaponTreeSkillGlacialCascadeIceNova")!.Fits(bow));
        Assert.True(bowNode.Fits(bow));
        Assert.False(bowNode.Fits(sceptre));
        Assert.True(oneHand.Fits(sceptre));
        Assert.False(oneHand.Fits(twoHanded));

        // A shorter prefix lights fewer nodes and adds fewer stats; a tree that short has no layer 4 to allocate.
        var type2 = Base(Sceptre);
        var full = CrucibleTrees.Generate(type2, 100, new Random(3));
        var item = ItemCatalog.Create(type2) with { WeaponTree = full };
        int plain = ItemModStats.Values(ItemCatalog.Create(type2), Program.Items.Mods).Count();

        int all = ItemModStats.Values(item, Program.Items.Mods).Count();
        Assert.True(all > plain);
        Assert.Equal(5, full.AllocatedMods.Count);

        var two = CrucibleTrees.Allocate(full, 2);
        Assert.Equal(2, two.AllocatedMods.Count);
        Assert.Equal<byte>([0, 0, 0xff, 0xff, 0xff], two.TierPicks);
        int shorter = ItemModStats.Values(item with { WeaponTree = two }, Program.Items.Mods).Count();
        Assert.InRange(shorter, plain + 1, all - 1);

        Assert.Throws<ArgumentException>(() => CrucibleTrees.Allocate(three, 4));
    }
}
