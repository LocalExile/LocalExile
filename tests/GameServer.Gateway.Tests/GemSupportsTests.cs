using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GemSupportsTests
{
    static string Data => FindData();
    static string FindData()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/GrantedEffects.json"))) return Path.Combine(dir.FullName, "data");
        throw new InvalidOperationException("Extract game tables before running gem support tests");
    }

    [Theory]
    [InlineData("Cyclone", "SupportIncreasedAreaOfEffect", true)]
    public void Compatibility_uses_native_skill_types(string active, string support, bool expected)
    {
        var skills = new SkillCatalog(Data);
        var catalog = new GemSupports(Data);
        var result = catalog.Resolve(skills.EffectRow(active), [new(skills.EffectRow(support), 20, 0, 1)], skills);
        Assert.Equal(expected, result.Count == 1);
    }

}
