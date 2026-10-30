using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// A gem's level comes from its experience, its skill's numbers from that level's rows and its quality, and its variant
// picks the skill (Agents/docs/protocol/19-gems.md). Reads the extracted game tables.
public class GemLevelsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly ItemCatalog Items = new(Data());
    static readonly SkillCatalog Skills = new(Data());
    static readonly StatCatalog Stats = new(Data());

    static ItemCatalog.GemType GemOf(string name) => Items.Gem(Items.FindGem(name)!.Value.Type)!;

    static Item Gem(string name, int level = 1, int quality = 0)
    {
        var (type, variant) = Items.FindGem(name)!.Value;
        return ItemCatalog.Create(type) with
        {
            GemExperience = Items.Gem(type)!.ExperienceFor(level), GemVariant = variant.Index, Quality = (byte)quality,
        };
    }

    static string[] Skills_(Item gem) => [.. Items.GemEffects(gem).Select(e => Skills.EffectId(e)!)];

    static int Stat(int effect, int level, int quality, string name) =>
        Skills.EffectStats(effect, level, quality).FirstOrDefault(kv => kv.Item1 == Stats.Key(name)).Item2;

    [Fact]
    public void The_level_follows_the_experience_up_to_the_cap()
    {
        var fireball = GemOf("Fireball");

        Assert.Equal(20, fireball.MaxLevel);
        Assert.Equal(1, fireball.Level(0));
        Assert.Equal(1, fireball.Level(69));
        Assert.Equal(2, fireball.Level(70));
        Assert.Equal(20, fireball.Level(int.MaxValue));
        Assert.Equal(21, fireball.Level(-1));
        for (int level = 1; level <= 20; level++) Assert.Equal(level, fireball.Level(fireball.ExperienceFor(level)));
        Assert.Equal(7, Items.GemLevel(Gem("Fireball", 7)));
    }

}
