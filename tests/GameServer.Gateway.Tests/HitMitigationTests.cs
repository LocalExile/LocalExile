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

    [Fact]
    public void Without_the_tables_every_type_reads_zero_and_the_hit_lands_unchanged()
    {
        var catalog = new MonsterCatalog(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var hit = DamagePacket.Of(DamageType.Fire, 37, 37);
        Assert.Equal(37, HitMitigation.RollMitigated(hit, type => HitMitigation.Resistance(catalog, Type(), 1, type), new Random(1)));
        // No variety at all (a monster made without one) is the same unmitigated case.
        Assert.Equal(37, HitMitigation.RollMitigated(hit, type => HitMitigation.Resistance(catalog, null, 1, type), new Random(1)));
    }

    [Fact]
    public void An_unresisted_hit_rolls_and_sums_exactly_as_RollTotal_did()
    {
        // The same seed must give the same draws: one roll per type, nothing consumed for the zero types.
        var hit = DamagePacket.Of(DamageType.Physical, 3, 10)
            .Add(DamageType.Fire, new DamageRange(2, 8))
            .Add(DamageType.Chaos, new DamageRange(1, 4));
        Assert.Equal(hit.RollTotal(new Random(20_260_126)),
            HitMitigation.RollMitigated(hit, _ => default, new Random(20_260_126)));
    }

    [Fact]
    public void A_mixed_rolled_hit_stays_fractional_per_type_until_the_total()
    {
        // A rolled 3 fire + 3 cold at 50% each: the components stay 1.5 and the total rounds once to 3, not 2 + 2 = 4.
        var hit = DamagePacket.Of(DamageType.Fire, 3, 3).Add(DamageType.Cold, new DamageRange(3, 3));
        Assert.Equal(3, HitMitigation.RollMitigated(hit, _ => new DamageModel.ResistanceValue(50), new Random(1)));
    }

    [Fact]
    public void A_real_fire_skeleton_takes_the_difficulty_columns_resistance_on_a_fixed_roll()
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        var catalog = new MonsterCatalog(data);
        var skeleton = catalog.Find("Metadata/Monsters/Skeletons/SkeletonCasterFire")!;
        // Level 46 (cruel difficulty): 20% fire resistance, everything else 0.
        Func<DamageType, DamageModel.ResistanceValue> resistance =
            type => HitMitigation.Resistance(catalog, skeleton, 46, type);
        Assert.Equal(80, HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Fire, 100, 100), resistance, new Random(7)));
        Assert.Equal(100, HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Cold, 100, 100), resistance, new Random(7)));
        // The same monster at level 68 (merciless): 40% fire.
        Assert.Equal(60, HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Fire, 100, 100),
            type => HitMitigation.Resistance(catalog, skeleton, 68, type), new Random(7)));
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
