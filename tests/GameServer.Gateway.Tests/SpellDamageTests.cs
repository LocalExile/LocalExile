namespace GameServer.Gateway.Tests;

public class SpellDamageTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) => name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Fact]
    public void Fireball_level_1_without_increases_is_its_base()
    {
        Assert.Equal((9, 14), SpellDamage.Hit(Stats(("spell_minimum_base_fire_damage", 9), ("spell_maximum_base_fire_damage", 14))));
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
