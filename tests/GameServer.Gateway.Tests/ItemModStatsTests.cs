using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemModStatsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(ItemCatalog Catalog, StatCatalog Stats)> Setup = new(() => (new ItemCatalog(Data()), new StatCatalog(Data())));
    static ModCatalog Mods => Setup.Value.Catalog.Mods;

    static Item Ring(params (string Id, int[] Values)[] mods) =>
        new(1, ["Base", "Mods"], 0) { Mods = [.. mods.Select(m => Mods.ById(m.Id)!.With(ModSection.Explicit, m.Values, Random.Shared))] };

    [Fact]
    public void Global_stats_of_equipped_items_are_summed_by_client_key()
    {
        var stats = ItemModStats.Global([Ring(("IncreasedLife1", [9])), Ring(("IncreasedLife1", [6]))], Mods, Setup.Value.Stats);
        Assert.Equal(15, stats[Setup.Value.Stats.Key("base_maximum_life")!.Value]);
    }

    [Fact]
    public void Local_stats_stay_on_their_item()
    {
        var wand = Ring(("LocalIncreasedAttackSpeed1", [6]), ("IncreasedLife1", [9]));
        Assert.DoesNotContain(Setup.Value.Stats.Key("local_attack_speed_+%")!.Value, ItemModStats.Global([wand], Mods, Setup.Value.Stats).Keys);
        var local = ItemModStats.Local(wand, Mods);
        Assert.Equal(6, local["local_attack_speed_+%"]);
        Assert.False(local.ContainsKey("base_maximum_life"));
    }

    [Fact]
    public void Implicits_and_unidentified_items_count_too()
    {
        var wand = ItemCatalog.Create(Setup.Value.Catalog.Row(384)!) with { Identified = false };
        int spell = wand.Mods.Single().Values[0];
        Assert.Equal(spell, ItemModStats.Global([wand], Mods, Setup.Value.Stats)[Setup.Value.Stats.Key("spell_damage_+%")!.Value]);
    }

    [Fact]
    public void Item_life_raises_the_pools_like_passive_life()
    {
        var witch = SkillCatalog.ClassInfo.Witch;
        var without = CharacterStats.For(witch, 10);
        var with = CharacterStats.For(witch, 10, new CharacterStats.Bonus(Life: 15));
        Assert.Equal(without.Life + 15, with.Life);
    }

    [Fact]
    public void The_equipment_rows_are_the_worn_ones_without_flasks_or_the_second_weapon_set()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], ItemModStats.EquipmentRows.Select(r => (int)r));
        Assert.DoesNotContain(PlayerInventories.Flask, ItemModStats.EquipmentRows);
    }
}
