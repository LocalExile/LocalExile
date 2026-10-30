namespace GameServer.Gateway.Tests;

public class PassiveTreeTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
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

}
