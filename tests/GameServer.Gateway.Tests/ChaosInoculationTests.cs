using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ChaosInoculationTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Maximum_life_becomes_one_and_energy_shield_is_untouched()
    {
        var normal = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 50);
        var ci = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 50, new CharacterStats.Bonus(ChaosInoculation: true));

        Assert.True(normal.Life > 1);
        Assert.Equal(1u, ci.Life);
        Assert.Equal(normal.EnergyShield, ci.EnergyShield);
    }

    [Fact]
    public void The_keystone_is_on_the_tree_and_grants_its_stat()
    {
        var tree = new PassiveTree(Data());
        var catalog = new StatCatalog(Data());
        var stats = tree.Stats([11455]);

        Assert.Equal("Chaos Inoculation", tree.Find(11455)?.Name);
        Assert.Equal(1, stats[catalog.Key("keystone_chaos_inoculation")!.Value]);
    }

    [Fact]
    public void The_keystone_makes_the_player_immune_to_chaos_damage()
    {
        var immune = DamageImmunities.From(name => name == "keystone_chaos_inoculation" ? 1 : 0);

        Assert.Contains(DamageType.Chaos, immune);
        Assert.Single(immune);
    }

    [Fact]
    public void The_panel_shows_the_chaos_immunity_row()
    {
        var row = PanelStats.Sums.First(r => r.Display == "chaos_damage_immunity");

        Assert.Contains(row.Sources, s => s.Name == "keystone_chaos_inoculation");
    }
}
