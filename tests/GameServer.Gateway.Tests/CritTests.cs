namespace GameServer.Gateway.Tests;

public class CritTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void A_weapons_base_critical_chance_comes_from_weapon_types()
    {
        var catalog = new ItemCatalog(Data());
        var wand = catalog.Row(384);
        Assert.NotNull(wand);
        Assert.Equal(830, catalog.WeaponCritical(wand));
    }

    [Fact]
    public void Critical_bonus_reaches_the_pools()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(CriticalStrikeChancePct: 100, SpellCriticalStrikeChancePct: 50,
                CriticalStrikeMultiplier: 30, SpellCriticalStrikeMultiplier: 20));

        Assert.Equal(100, pools.CriticalStrikeChancePct);
        Assert.Equal(180, pools.CriticalStrikeMultiplier);
        Assert.Equal(170, pools.SpellCriticalStrikeMultiplier);
        Assert.Equal(750, pools.SpellCriticalStrikeChance); // 5% base, 50% increased (permyriad)
    }
}
