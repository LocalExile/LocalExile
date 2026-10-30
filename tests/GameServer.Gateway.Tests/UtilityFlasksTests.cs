using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UtilityFlasksTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog(Data()));
    static readonly Lazy<StatCatalog> StatNames = new(() => new StatCatalog(Data()));

    static UtilityFlasks.Utility? Of(string name) => Catalog.Value.UtilityFlaskOf(Catalog.Value.Find(name)!);

    static UtilityFlasks.Utility Utility(params (string, int)[] stats) =>
        new(0, 121, "test", stats, 6.0, 30, 60);

    static Item Flask(int charges = 60, byte quality = 0, ItemRarity rarity = ItemRarity.Normal) =>
        new(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Charges = charges, Quality = quality, Rarity = rarity };

    static int None(string _) => 0;

    static IEnumerable<(string, int)> Plain(IEnumerable<UtilityFlasks.Source> stats) => stats.Select(s => (s.Stat, s.Value));

    [Fact]
    public void The_flasks_come_from_Flasks_dat_BuffDefinitions_and_ComponentCharges()
    {
        var quicksilver = Of("Quicksilver Flask")!;
        Assert.Equal(121, quicksilver.BuffRow);
        Assert.Equal("flask_utility_sprint", quicksilver.BuffId);
        Assert.Equal([("base_movement_velocity_+%", 40)], quicksilver.Stats);
        // RecoveryTime (60), as Path of Building and RePoE 3.29 give it: 6 s, not RecoveryTime2 (20).
        Assert.Equal(6.0, quicksilver.Seconds);
        Assert.Equal((30, 60), (quicksilver.ChargesPerUse, quicksilver.MaxCharges));

        var ruby = Of("Ruby Flask")!;
        Assert.Equal("flask_utility_resist_fire", ruby.BuffId);
        Assert.Equal([("base_fire_damage_resistance_%", 40), ("base_maximum_fire_damage_resistance_%", 5)], ruby.Stats);
        Assert.Equal(8.0, ruby.Seconds);

        Assert.Equal([("base_physical_damage_reduction_rating", 1500)], Of("Granite Flask")!.Stats);
        Assert.Equal([("base_evasion_rating", 1500)], Of("Jade Flask")!.Stats);
        Assert.Equal([("base_chaos_damage_resistance_%", 35)], Of("Amethyst Flask")!.Stats);
        Assert.Equal([("base_resist_all_elements_%", 35)], Of("Bismuth Flask")!.Stats);
        Assert.Equal([("basalt_flask_armour_+%_final", 20)], Of("Basalt Flask")!.Stats);
        // Life and mana flasks and other items are not utility flasks.
        Assert.Null(Of("Small Life Flask"));
        Assert.Null(Catalog.Value.UtilityFlaskOf(Catalog.Value.Row(384)!));   // Driftwood Wand
    }

    static UtilityFlasks.Active Active(uint item, UtilityFlasks.Utility u, double started, ushort buff = 1, ushort? extra = null) =>
        new(item, (byte)item, "test", u, buff, extra, Sources(u), started, started + u.Seconds);

    static List<UtilityFlasks.Source> Sources(UtilityFlasks.Utility u) =>
        [.. UtilityFlasks.For(u, Flask(), new Dictionary<string, int>(), None).Stats];

    // The character as PoolsFor makes it, with the running flasks (Apply), and the walk estimate from its pools.
    static void Recompute(ClientState conn)
    {
        conn.Pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, UtilityFlasks.Apply(CharacterStats.Bonus.None, conn.UtilityFlaskEffects.Stats()));
        conn.ApplyMovementSpeed();
    }

    static ClientState Connection() => new(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);

}
