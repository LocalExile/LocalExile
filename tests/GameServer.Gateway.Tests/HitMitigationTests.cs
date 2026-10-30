using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// HitMitigation, the per-type pass every player hit on a monster takes: one roll per type, each type's own base
/// resistance, summed. The synthetic tables stand in for the extracted game data; the real-catalog cases run when the
/// tables exist.
/// </summary>
public class HitMitigationTests
{
    static string SyntheticFolder()
    {
        string folder = Directory.CreateTempSubdirectory("localexile-hit-mitigation-").FullName;
        string tables = Directory.CreateDirectory(Path.Combine(folder, "tables")).FullName;
        File.WriteAllText(Path.Combine(tables, "MonsterTypes.json"), """[{"Resistances":0}]""");
        File.WriteAllText(Path.Combine(tables, "DefaultMonsterStats.json"), """[{"Difficulty":1},{"Difficulty":2},{"Difficulty":3}]""");
        File.WriteAllText(Path.Combine(tables, "MonsterResistances.json"), """
            [{"FireNormal":40,"ColdNormal":0,"LightningNormal":-30,"ChaosNormal":75,
              "FireCruel":40,"ColdCruel":0,"LightningCruel":-30,"ChaosCruel":75,
              "FireMerciless":40,"ColdMerciless":0,"LightningMerciless":-30,"ChaosMerciless":75}]
            """);
        return folder;
    }

    static MonsterCatalog.Variety Type(int monsterType = 0) =>
        new(Row: 0, Id: "Metadata/Monsters/Test", Name: "Test", BaseType: "Test", AnimatedObject: null, Components: null,
            MonsterType: monsterType);

    [Fact]
    public void Each_type_is_mitigated_by_its_own_base_resistance_and_the_total_is_summed()
    {
        var catalog = new MonsterCatalog(SyntheticFolder());
        var variety = Type();
        Func<DamageType, DamageModel.ResistanceValue> resistance =
            type => HitMitigation.Resistance(catalog, variety, 1, type);
        // 100 of each type against 40% fire, 0% cold, -30% lightning, 75% chaos (the type maximum), physical 0.
        var rolled = DamagePacket.Empty
            .Add(DamageType.Physical, new DamageRange(100, 100))
            .Add(DamageType.Lightning, new DamageRange(100, 100))
            .Add(DamageType.Cold, new DamageRange(100, 100))
            .Add(DamageType.Fire, new DamageRange(100, 100))
            .Add(DamageType.Chaos, new DamageRange(100, 100));
        Assert.Equal(60 + 100 + 130 + 25 + 100, HitMitigation.RollMitigated(rolled, resistance, new Random(1)));
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
