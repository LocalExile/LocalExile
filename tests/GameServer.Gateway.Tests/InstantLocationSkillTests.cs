namespace GameServer.Gateway.Tests;

public class InstantLocationSkillTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly SkillCatalog Skills = new(Data());
    static int NoStats(string _) => 0;
    static ClientState Connection(Func<int, int, bool>? walkable = null) =>
        new(new AreaLayout(1, "test", 300, 300, 100, 100, walkable ?? ((_, _) => true), []), 1, 1)
        { PlayerSent = true, PlayerLife = 100 };

    [Theory]
    [InlineData("Frostblink", true, true)]
    public void Catalog_classification_keeps_noninstant_and_unimplemented_movement_off_both_routes(
        string effectId, bool location, bool objectTarget)
    {
        int effect = Skills.EffectRow(effectId);
        Assert.Equal(location, InstantLocationSkill.Supported(Skills, effect));
        Assert.Equal(objectTarget, InstantLocationSkill.ObjectTargetSupported(Skills, effect));
    }

}
