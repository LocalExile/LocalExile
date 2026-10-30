using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Flask charge gains (FlaskCharges): poewiki's Flask page ("Flask charges") with the monster rarity mods of Mods.dat.
public class FlaskChargesTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ModCatalog> Mods = new(() => new ItemCatalog(Data()).Mods);
    static Dictionary<string, int> Local(params (string Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);
    static Func<string, int> Character(params (string Stat, int Value)[] stats) => name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    [Fact]
    public void Kills_give_1_3_5_6_and_11_charges_by_rarity_from_the_monster_mods()
    {
        // Mods.dat MonsterMagic6 / MonsterRare6 / MonsterUnique6: +250 / +500 / +1000% flask charges granted.
        Assert.Equal(1, FlaskCharges.KillCharges(MonsterRarity.Normal, Mods.Value));
        Assert.Equal(3.5, FlaskCharges.KillCharges(MonsterRarity.Magic, Mods.Value));
        Assert.Equal(6, FlaskCharges.KillCharges(MonsterRarity.Rare, Mods.Value));
        Assert.Equal(11, FlaskCharges.KillCharges(MonsterRarity.Unique, Mods.Value));
    }

}
