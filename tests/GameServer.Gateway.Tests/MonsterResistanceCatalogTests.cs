using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterResistanceCatalogTests
{
    [Fact]
    public void Synthetic_tables_resolve_the_type_row_difficulty_and_individual_damage_type_without_a_client()
    {
        string folder = Directory.CreateTempSubdirectory("localexile-monster-resistance-").FullName;
        string tables = Directory.CreateDirectory(Path.Combine(folder, "tables")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(tables, "MonsterTypes.json"), """[{"Resistances":0}]""");
            File.WriteAllText(Path.Combine(tables, "DefaultMonsterStats.json"), """[{"Difficulty":1},{"Difficulty":2},{"Difficulty":3}]""");
            File.WriteAllText(Path.Combine(tables, "MonsterResistances.json"), """
                [{"FireNormal":10,"ColdNormal":20,"LightningNormal":30,"ChaosNormal":40,
                  "FireCruel":11,"ColdCruel":21,"LightningCruel":31,"ChaosCruel":41,
                  "FireMerciless":12,"ColdMerciless":22,"LightningMerciless":32,"ChaosMerciless":42}]
                """);
            var catalog = new MonsterResistanceCatalog(folder);
            Assert.Equal(10, catalog.BaseResistance(0, 1, DamageType.Fire));
            Assert.Equal(21, catalog.BaseResistance(0, 2, DamageType.Cold));
            Assert.Equal(32, catalog.BaseResistance(0, 3, DamageType.Lightning));
            Assert.Equal(42, catalog.BaseResistance(0, 3, DamageType.Chaos));
            Assert.Equal(0, catalog.BaseResistance(0, 1, DamageType.Physical));
            Assert.Null(catalog.BaseResistance(1, 1, DamageType.Fire));
            Assert.Null(catalog.BaseResistance(0, 4, DamageType.Fire));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(45, 0)]
    [InlineData(46, 20)]
    [InlineData(67, 20)]
    [InlineData(68, 40)]
    public void Fire_skeletons_use_the_levels_actual_difficulty_column(int level, int expected)
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        var catalog = new MonsterCatalog(data);
        var skeleton = catalog.Find("Metadata/Monsters/Skeletons/SkeletonCasterFire")!;
        Assert.Equal(expected, catalog.BaseResistance(skeleton, level, DamageType.Fire));
        Assert.Equal(0, catalog.BaseResistance(skeleton, level, DamageType.Cold));
        Assert.Equal(0, catalog.BaseResistance(skeleton, level, DamageType.Chaos));
        Assert.Equal(0, catalog.BaseResistance(skeleton, level, DamageType.Physical));
    }

    [Fact]
    public void Map_boss_resistance_is_not_the_same_as_the_act_one_boss()
    {
        if (Data() is not { } data) return;
        var catalog = new MonsterCatalog(data);
        var hillock = catalog.Find("Metadata/Monsters/ZombieBoss/ZombieBossHillock")!;
        var map = catalog.Find("Metadata/Monsters/ZombieBoss/MapZombieBossHillock")!;
        Assert.Equal(0, catalog.BaseResistance(hillock, 1, DamageType.Fire));
        Assert.Equal(40, catalog.BaseResistance(map, 68, DamageType.Fire));
        Assert.Equal(25, catalog.BaseResistance(map, 68, DamageType.Chaos));
    }

    [Fact]
    public void Invalid_or_missing_source_data_is_distinguishable_from_zero_resistance()
    {
        if (Data() is { } data)
        {
            var catalog = new MonsterResistanceCatalog(data);
            Assert.Null(catalog.BaseResistance(-1, 1, DamageType.Fire));
            Assert.Null(catalog.BaseResistance(0, 101, DamageType.Fire));
        }
        Assert.Null(new MonsterResistanceCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))
            .BaseResistance(0, 1, DamageType.Fire));
    }

    private static string? Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "data");
            string[] required = ["MonsterVarieties", "MonsterTypes", "MonsterResistances", "DefaultMonsterStats"];
            if (required.All(name => File.Exists(Path.Combine(data, "tables", name + ".json")))) return data;
        }
        return null;
    }
}
