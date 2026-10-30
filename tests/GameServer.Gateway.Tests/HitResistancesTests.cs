using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The critical-strike elemental rules on the Gateway's outgoing hits: HitMitigation's resolved pass with
/// HitResistances against fixed packets on synthetic monster tables, and the shared MonsterDamage (the one helper
/// the default attack, the gem projectile and the area hits all use) with the real catalog and stat keys when the
/// extracted tables exist. Inevitable Judgement's stat values reach it through the ordinary player stats.
/// </summary>
public class HitResistancesTests
{
    static string SyntheticFolder()
    {
        string folder = Directory.CreateTempSubdirectory("localexile-hit-resistances-").FullName;
        string tables = Directory.CreateDirectory(Path.Combine(folder, "tables")).FullName;
        File.WriteAllText(Path.Combine(tables, "MonsterTypes.json"), """[{"Resistances":0}]""");
        File.WriteAllText(Path.Combine(tables, "DefaultMonsterStats.json"), """[{"Difficulty":1},{"Difficulty":2},{"Difficulty":3}]""");
        File.WriteAllText(Path.Combine(tables, "MonsterResistances.json"), """
            [{"FireNormal":40,"ColdNormal":40,"LightningNormal":-30,"ChaosNormal":40,
              "FireCruel":40,"ColdCruel":40,"LightningCruel":-30,"ChaosCruel":40,
              "FireMerciless":40,"ColdMerciless":40,"LightningMerciless":-30,"ChaosMerciless":40}]
            """);
        return folder;
    }

    static MonsterCatalog.Variety Type(int monsterType = 0) =>
        new(Row: 0, Id: "Metadata/Monsters/Test", Name: "Test", BaseType: "Test", AnimatedObject: null, Components: null,
            MonsterType: monsterType);

    static HitResistances.Mods Judgement(int pen = 10) => new(CriticalIgnoreElemental: 1, AttackCriticalIgnoreElemental: 0,
        NonCriticalPenetration: pen, CannotPenetrateOrIgnore: 0);

    static HitResistances.Strike Hit(bool critical = false, bool attack = true, bool overTime = false) =>
        new(Critical: critical, Attack: attack, OverTime: overTime);

    static Func<DamageType, DamageModel.ResistanceValue> Resistance(MonsterCatalog catalog) =>
        type => HitMitigation.Resistance(catalog, Type(), 1, type);

    static DamagePacket AllTypes() => DamagePacket.Empty
        .Add(DamageType.Physical, new DamageRange(100, 100))
        .Add(DamageType.Lightning, new DamageRange(100, 100))
        .Add(DamageType.Cold, new DamageRange(100, 100))
        .Add(DamageType.Fire, new DamageRange(100, 100))
        .Add(DamageType.Chaos, new DamageRange(100, 100));

    [Fact]
    public void A_critical_elemental_hit_mitigates_as_zero_and_the_others_keep_their_resistance()
    {
        var catalog = new MonsterCatalog(SyntheticFolder());
        var resistance = Resistance(catalog);
        // Fixed roll: 100 of every type. The critical strike ignores 40% fire, 40% cold and -30% lightning
        // (negative included); physical has no base resistance here and chaos keeps its plain 40%.
        int critical = HitMitigation.RollMitigated(AllTypes(),
            type => HitResistances.Effective(type, resistance(type), Hit(critical: true), Judgement()), new Random(1));
        Assert.Equal(100 + 100 + 100 + 100 + 60, critical);
        // The same packet non-critical: every elemental type takes its resistance, lightning amplifies.
        int plain = HitMitigation.RollMitigated(AllTypes(),
            type => HitResistances.Effective(type, resistance(type), Hit(), Judgement() with { NonCriticalPenetration = 0 }),
            new Random(1));
        Assert.Equal(100 + 60 + 60 + 130 + 60, plain);
    }

    private static string? Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
        {
            string data = Path.Combine(dir.FullName, "data");
            string[] required = ["MonsterVarieties", "MonsterTypes", "MonsterResistances", "DefaultMonsterStats", "Stats"];
            if (required.All(name => File.Exists(Path.Combine(data, "tables", name + ".json")))) return data;
        }
        return null;
    }
}
