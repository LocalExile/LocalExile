namespace GameServer.Gateway.Tests;

public class PassiveTreeTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RepositoryRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Block_mastery_has_its_four_effects()
    {
        var tree = new PassiveTree(Data());
        Assert.True(tree.Loaded, Data());
        var node = tree.Find(5368);
        Assert.NotNull(node);
        Assert.Equal("Block Mastery", node.Name);
        Assert.Equal(new[] { 55013, 44840, 23442, 4500 }, tree.EffectsFor(5368).Select(e => (int)e.Hash));
    }

    [Theory]
    [InlineData(55013, 494, 2)]   // maximum_block_%
    [InlineData(44840, 8341, 2)]  // base_maximum_spell_block_%
    [InlineData(23442, 1371, 20)] // life_gained_on_block
    [InlineData(23442, 1372, 20)] // mana_gained_on_block
    [InlineData(4500, 21005, 1)]  // spell_block_%_per_5_attack_block
    public void A_selected_effect_adds_its_stats(int effect, uint key, int value)
    {
        var tree = new PassiveTree(Data());
        var stats = tree.Stats([5368], new Dictionary<ushort, ushort> { [5368] = (ushort)effect });
        Assert.Equal(value, stats[key]);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(100, 29119)]
    public void Gold_respec_costs_the_level_price_per_node(int level, long perNode)
    {
        var tree = new PassiveTree(Data());
        // 8135 and 11128: the two nodes refunded live; an ascendancy node (Witch Ascendancy, 51782) is not part of it.
        Assert.Equal(2 * perNode, tree.GoldRespecCost([8135, 11128], level));
        Assert.Equal(0, tree.GoldRespecCost([51782], level));
    }
}
