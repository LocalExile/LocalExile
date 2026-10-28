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

    [Fact]
    public void The_noncritical_penetration_subtracts_after_the_cap()
    {
        var catalog = new MonsterCatalog(SyntheticFolder());
        var resistance = Resistance(catalog);
        // 40% fire − 10 pen → 30%: 70 of 100. The -30% lightning − 10 → -40%: 140 of 100.
        int fire = HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Fire, 100, 100),
            type => HitResistances.Effective(type, resistance(type), Hit(), Judgement()), new Random(1));
        Assert.Equal(70, fire);
        int lightning = HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Lightning, 100, 100),
            type => HitResistances.Effective(type, resistance(type), Hit(), Judgement()), new Random(1));
        Assert.Equal(140, lightning);
    }

    [Fact]
    public void The_attack_only_ignore_stat_covers_attacks_but_not_spells()
    {
        var catalog = new MonsterCatalog(SyntheticFolder());
        var resistance = Resistance(catalog);
        var mods = new HitResistances.Mods(0, 1, 0, 0);
        int attack = HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Fire, 100, 100),
            type => HitResistances.Effective(type, resistance(type), Hit(critical: true, attack: true), mods), new Random(1));
        int spell = HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Fire, 100, 100),
            type => HitResistances.Effective(type, resistance(type), Hit(critical: true, attack: false), mods), new Random(1));
        Assert.Equal(100, attack);
        Assert.Equal(60, spell);
    }

    [Fact]
    public void The_suppressor_restores_the_plain_resistance_on_the_gateway_pass()
    {
        var catalog = new MonsterCatalog(SyntheticFolder());
        var resistance = Resistance(catalog);
        var mods = new HitResistances.Mods(1, 0, 10, 1);
        int critical = HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Fire, 100, 100),
            type => HitResistances.Effective(type, resistance(type), Hit(critical: true), mods), new Random(1));
        int plain = HitMitigation.RollMitigated(DamagePacket.Of(DamageType.Fire, 100, 100),
            type => HitResistances.Effective(type, resistance(type), Hit(), mods), new Random(1));
        Assert.Equal(60, critical);
        Assert.Equal(60, plain);
    }

    [Fact]
    public void The_mitigation_draws_nothing_and_keeps_the_roll_then_crit_order()
    {
        // The pre-change sequence: one roll per type, then the single crit draw. The hit path now rolls the packet
        // once, draws the crit in the same place, and mitigates the already-rolled packet: the same-seed result and
        // the following RNG state must match, because even a fixed range consumes a draw when rolled.
        var hit = DamagePacket.Of(DamageType.Fire, 3, 10).Add(DamageType.Physical, new DamageRange(2, 8));
        int seed = 20_261_026;
        var reference = new Random(seed);
        int expectedRolled = hit.RollTotal(reference);
        int expectedCritDraw = reference.Next(10_000);
        var random = new Random(seed);
        var rolled = hit.Roll(random);
        int critDraw = random.Next(10_000);
        int mitigated = HitMitigation.Mitigated(rolled, _ => default);
        Assert.Equal(expectedRolled, (int)rolled.MinTotal);
        Assert.Equal(expectedRolled, mitigated);
        Assert.Equal(expectedCritDraw, critDraw);
        // The mitigation consumed no draw: the next draw from the same stream matches the reference state.
        Assert.Equal(reference.Next(), random.Next());
        // With the rules active the same sequence also holds through the shared helper: the same three draws (the
        // packet roll and the crit draw), the deterministic mitigation, and the stream state right after them.
        var catalogue = new MonsterCatalog(SyntheticFolder());
        var monster = new SpawnedMonster("test", new WalkEstimator(0, 0, 37), 20, 2, 100) { Variety = Type(), Level = 1 };
        var ruled = new Random(seed);
        var rolledRuled = hit.Roll(ruled);
        bool criticalRuled = ruled.Next(10_000) < 10_000;
        var (damage, _) = Program.MonsterDamage(monster, rolledRuled, criticalRuled, attack: true, Judgement());
        var expected = HitMitigation.Mitigated(rolledRuled, type => HitResistances.Effective(type,
            HitMitigation.Resistance(catalogue, Type(), 1, type),
            new HitResistances.Strike(Critical: criticalRuled, Attack: true), Judgement()));
        Assert.Equal(expected, damage);
        var ruledReference = new Random(seed);
        hit.RollTotal(ruledReference);
        ruledReference.Next(10_000);
        Assert.Equal(ruledReference.Next(), ruled.Next());
    }

    [Fact]
    public void The_shared_monster_damage_resolves_the_real_skeletons_fire_with_the_stat_keys()
    {
        if (Data() is null) return; // Real-client cross-check is optional on a fresh clone.
        var skeleton = Program.Monsters.Find("Metadata/Monsters/Skeletons/SkeletonCasterFire")!;
        var m = new SpawnedMonster("skeleton", new WalkEstimator(0, 0, 37), 20, 2, 100) { Variety = skeleton, Level = 46 };
        // The keys as the player's stats carry them (Inevitable Judgement: ignore 1, penetration 10), read the same
        // way the hit paths read a stats dictionary.
        var judgement = Program.HitResistMods(new Dictionary<uint, int> { [3626] = 1, [3885] = 10 });
        // Level 46 (cruel) gives the skeleton 20% fire. A critical 100 fire hit ignores it entirely; the packet is
        // rolled once and the mitigation runs on the already-rolled packet.
        var rolled = DamagePacket.Of(DamageType.Fire, 100, 100).Roll(new Random(7));
        var (ignored, note) = Program.MonsterDamage(m, rolled, critical: true, attack: true, judgement);
        Assert.Equal(100, ignored);
        Assert.Equal("fire 20 ignored", note);
        // Non-critical keeps the 20% and the 10 penetration subtracts after the cap: 20 − 10 = 10%.
        var (penetrated, penNote) = Program.MonsterDamage(m, rolled, critical: false, attack: true, judgement);
        Assert.Equal(90, penetrated);
        Assert.Equal("fire 20→10 (pen 10)", penNote);
        // A critical hit with only the penetration stat takes no penetration (the stat's condition is a negated
        // CriticalStrike) and names nothing.
        var (critPen, critPenNote) = Program.MonsterDamage(m, rolled, critical: true, attack: true,
            Program.HitResistMods(new Dictionary<uint, int> { [3885] = 10 }));
        Assert.Equal(80, critPen);
        Assert.Null(critPenNote);
        // The suppressor keeps the plain 20% on the critical hit too.
        var (held, heldNote) = Program.MonsterDamage(m, rolled, critical: true, attack: true,
            Program.HitResistMods(new Dictionary<uint, int> { [3626] = 1, [3885] = 10, [12052] = 1 }));
        Assert.Equal(80, held);
        Assert.Equal("fire 20 (cannot penetrate or ignore)", heldNote);
        // Physical is outside the elemental rules even on a critical hit with the ignore (its base here is 0).
        var physicalRolled = DamagePacket.Of(DamageType.Physical, 100, 100).Roll(new Random(7));
        var (physical, physicalNote) = Program.MonsterDamage(m, physicalRolled, critical: true, attack: true, judgement);
        Assert.Equal(100, physical);
        Assert.Null(physicalNote);
    }

    [Fact]
    public void Combined_player_skill_penetration_and_critical_rules_share_fractional_target_mitigation()
    {
        var variety = Program.Monsters.Find("Metadata/Monsters/ZombieBoss/MapZombieBossHillock")!;
        var monster = new SpawnedMonster("map boss", new WalkEstimator(0, 0, 30), 60, 4, 100)
        { Variety = variety, Level = 68 };
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        uint fire = Key("base_reduce_enemy_fire_resistance_%");
        uint noncrit = Key("non_critical_strikes_penetrate_elemental_resistances_%");
        var player = new Dictionary<uint, int> { [fire] = 10, [noncrit] = 7,
            [Key("critical_strikes_ignore_elemental_resistances")] = 1 };
        var skill = new Dictionary<uint, int> { [fire] = 10, [noncrit] = 3 };
        var pen = Penetration.Of(player, Program.Stats, attack: true, skill);
        var mods = Program.HitResistMods(player, skill);
        Assert.Equal(20, pen.Fire);
        Assert.Equal(10, mods.NonCriticalPenetration);
        var rolled = DamagePacket.Of(DamageType.Fire, 3, 3).Add(DamageType.Cold, new(3, 3));
        Assert.Equal(5, Program.MonsterDamage(monster, rolled, false, true, mods, pen).Damage);
        Assert.Equal(6, Program.MonsterDamage(monster, rolled, true, true, mods, pen).Damage);
        skill[Key("cannot_penetrate_or_ignore_elemental_resistances")] = 1;
        mods = Program.HitResistMods(player, skill);
        Assert.Equal(4, Program.MonsterDamage(monster, rolled, true, true, mods, pen).Damage);
    }

    [Fact]
    public void Inevitable_judgement_grants_the_critical_ignore_and_the_ten_penetration()
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        // Graph node 48214 (AscendancyInquisitor8) grants exactly the two stats, read through the ordinary
        // passive stat sum.
        var tree = new PassiveTree(data);
        var catalog = new StatCatalog(data);
        var stats = tree.Stats([48214]);
        Assert.Equal(1, stats[catalog.Key("critical_strikes_ignore_elemental_resistances")!.Value]);
        Assert.Equal(10, stats[catalog.Key("non_critical_strikes_penetrate_elemental_resistances_%")!.Value]);
        // Their keys are the Stats.json rows + 1: 3625 → 3626, 3884 → 3885.
        Assert.Equal((uint)3626, catalog.Key("critical_strikes_ignore_elemental_resistances"));
        Assert.Equal((uint)3885, catalog.Key("non_critical_strikes_penetrate_elemental_resistances_%"));
    }

    [Fact]
    public void The_attack_only_ignore_and_the_suppressor_have_their_exact_stat_keys()
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        var catalog = new StatCatalog(data);
        // Stats rows 12971 and 12051 in the 3.29.3b Stats.json.
        Assert.Equal((uint)12972, catalog.Key("attack_critical_strikes_ignore_elemental_resistances"));
        Assert.Equal((uint)12052, catalog.Key("cannot_penetrate_or_ignore_elemental_resistances"));
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
