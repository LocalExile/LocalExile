namespace GameServer.Gateway.Tests;

public class SpellDamageTests
{
    [Fact]
    public void Weighted_average_keeps_fractions_and_exact_rate_until_the_native_integer_boundary()
    {
        // Average 14.03, half of hits deal double, actual 750ms action duration.
        Assert.Equal((2104, 2806), SpellDamage.Display((10.98, 17.08), 1 / .75, 1.5));
        Assert.Equal((2104, 0), SpellDamage.Display((10.98, 17.08), -1, 1.5));
        Assert.Equal((0, 0), SpellDamage.Display((0, 0), 100, 100));
    }

    [Fact]
    public void Huge_native_damage_values_saturate_instead_of_wrapping_negative()
    {
        Assert.Equal((int.MaxValue, int.MaxValue), SpellDamage.Display((int.MaxValue, int.MaxValue), 30, 3));
        Assert.Equal((0, 0), SpellDamage.Display((-10, -5), 3));
    }

    static Func<string, int> Stats(params (string Name, int Value)[] stats) => name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Fact]
    public void Fireball_level_1_without_increases_is_its_base()
    {
        Assert.Equal((9, 14), SpellDamage.Hit(Stats(("spell_minimum_base_fire_damage", 9), ("spell_maximum_base_fire_damage", 14))));
    }

    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }
}
