namespace GameServer.Gateway.Tests;

public class ArmourEvasionTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Armour_is_flat_armour_raised_by_increased_armour()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(Armour: 100, ArmourPct: 50));

        Assert.Equal(150, pools.Armour);
        Assert.Equal(50, pools.ArmourPct);
    }

    [Fact]
    public void Evasion_keeps_its_base_flat_and_dexterity_increases()
    {
        // Witch dexterity 14: 2% from dexterity. 15 base + 100 flat = 115, raised by 2% + 50% = 174.
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(Evasion: 100, EvasionPct: 50));

        Assert.Equal(174, pools.Evasion);
        Assert.Equal(52, pools.EvasionPct);
    }

    [Fact]
    public void Base_items_expose_the_ArmourTypes_ranges()
    {
        var catalog = new ItemCatalog(Data());

        Assert.Equal(new ItemCatalog.BaseDefences(19, 27, 0, 0), catalog.Defences(catalog.Find("Plate Vest")!));
        Assert.Equal(new ItemCatalog.BaseDefences(27, 38, 27, 38), catalog.Defences(catalog.Find("Scale Vest")!));
        Assert.Equal(new ItemCatalog.BaseDefences(0, 0, 13, 18), catalog.Defences(catalog.Find("Rawhide Gloves")!));
        Assert.Equal(new ItemCatalog.BaseDefences(0, 0, 0, 0, 14, 20), catalog.Defences(catalog.Find("Simple Robe")!));
    }

    [Fact]
    public void Cloth_and_Chain_uses_the_combined_armour_and_evasion_increase()
    {
        var tree = new PassiveTree(Data());
        var catalog = new StatCatalog(Data());
        var stats = tree.Stats([21435]);

        Assert.Equal("Cloth and Chain", tree.Find(21435)?.Name);
        Assert.Equal(24, stats[catalog.Key("evasion_and_physical_damage_reduction_rating_+%")!.Value]);
    }

    [Fact]
    public void Iron_Reflexes_converts_evasion_to_armour_and_drops_dexterity_evasion()
    {
        // Witch dexterity 14 would normally add 2% evasion. Iron Reflexes removes that and converts the
        // 15 base + 100 flat evasion at the armour/evasion increases: 100 × 1.50 + 115 × 2.00 = 380.
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(Armour: 100, ArmourPct: 50, Evasion: 100, EvasionPct: 50,
                IronReflexes: true));

        Assert.Equal(380, pools.Armour);
        Assert.Equal(0, pools.Evasion);
        Assert.Equal(50, pools.EvasionPct);
    }

    [Fact]
    public void Iron_Reflexes_is_the_keystone_stat_on_the_tree()
    {
        var tree = new PassiveTree(Data());
        var catalog = new StatCatalog(Data());
        var stats = tree.Stats([10661]);

        Assert.Equal("Iron Reflexes", tree.Find(10661)?.Name);
        Assert.Equal(1, stats[catalog.Key("keystone_iron_reflexes")!.Value]);
    }
}
