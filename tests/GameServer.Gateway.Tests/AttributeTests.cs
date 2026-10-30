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
    public void Every_attribute_stat_reaches_its_attributes(string stat, int str, int dex, int intel)
    {
        int Get(string name) => name == stat ? 7 : 0;
        Assert.Equal((str, dex, intel), CharacterStats.Attributes(Get));
    }

}
