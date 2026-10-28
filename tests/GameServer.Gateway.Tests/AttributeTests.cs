namespace GameServer.Gateway.Tests;

public class AttributeTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    // Practical Application (8135) gives +20 to Strength and Dexterity through base_strength_and_dexterity; the
    // other two base_*_and_* pair stats are on 12 more nodes.
    [Theory]
    [InlineData("base_strength_and_dexterity", 7, 7, 0)]
    [InlineData("base_strength_and_intelligence", 7, 0, 7)]
    [InlineData("base_dexterity_and_intelligence", 0, 7, 7)]
    [InlineData("additional_strength_and_dexterity", 7, 7, 0)]
    [InlineData("additional_all_attributes", 7, 7, 7)]
    public void Every_attribute_stat_reaches_its_attributes(string stat, int str, int dex, int intel)
    {
        int Get(string name) => name == stat ? 7 : 0;
        Assert.Equal((str, dex, intel), CharacterStats.Attributes(Get));
    }

    [Fact]
    public void Practical_application_gives_strength_and_dexterity()
    {
        var tree = new PassiveTree(Data());
        var names = new StatCatalog(Data());
        var stats = tree.Stats([8135]);
        int Get(string name) => names.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        Assert.Equal((20, 20, 0), CharacterStats.Attributes(Get));
    }
}
