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

    private static string? Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "data");
            string[] required = ["MonsterVarieties", "MonsterTypes", "MonsterResistances", "DefaultMonsterStats"];
            if (required.All(name => File.Exists(Path.Combine(data, "tables", name + ".json")))) return data;
        }
        return null;
    }
}
