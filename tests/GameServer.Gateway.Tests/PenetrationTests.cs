using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Penetration, the unconditional resistance-penetration stats of the player's hits: the general stats on every hit,
/// the attack-only ones on attacks, the "cannot penetrate" flag turning the elemental ones off. The synthetic
/// Stats.json stands in for the extracted table (keys follow the row order); the real-table cases run when it exists.
/// </summary>
public class PenetrationTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("localexile-penetration-").FullName;
    readonly StatCatalog _catalog;
    readonly Dictionary<uint, int> _stats = [];

    public PenetrationTests()
    {
        string tables = Directory.CreateDirectory(Path.Combine(_folder, "tables")).FullName;
        File.WriteAllText(Path.Combine(tables, "Stats.json"), """
            [{"Id":"base_reduce_enemy_fire_resistance_%"},{"Id":"base_reduce_enemy_cold_resistance_%"},{"Id":"base_reduce_enemy_lightning_resistance_%"},
             {"Id":"base_penetrate_elemental_resistances_%"},{"Id":"base_attack_damage_penetrates_fire_resist_%"},{"Id":"base_attack_damage_penetrates_cold_resist_%"},
             {"Id":"base_attack_damage_penetrates_lightning_resist_%"},{"Id":"base_attack_damage_penetrates_elemental_resist_%"},{"Id":"base_attack_damage_penetrates_chaos_resist_%"},
             {"Id":"cannot_penetrate_or_ignore_elemental_resistances"},
             {"Id":"main_hand_attack_damage_penetrates_fire_resist_%"},{"Id":"local_fire_penetration_%"},{"Id":"non_critical_strikes_penetrate_elemental_resistances_%"},
             {"Id":"reduce_enemy_elemental_resistance_%"},{"Id":"reduce_enemy_chaos_resistance_%"}]
            """);
        _catalog = new StatCatalog(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    void Set(string name, int value)
    {
        var key = _catalog.Key(name) ?? throw new InvalidOperationException(name);
        _stats[key] = value;
    }

    Penetration.PerType Attack() => Penetration.Of(_stats, _catalog, attack: true);
    Penetration.PerType Spell() => Penetration.Of(_stats, _catalog, attack: false);

    [Fact]
    public void General_type_and_attack_stats_sum_and_a_spell_takes_only_the_general_ones()
    {
        Set("base_reduce_enemy_fire_resistance_%", 10);
        Set("base_penetrate_elemental_resistances_%", 5);
        Set("base_attack_damage_penetrates_fire_resist_%", 7);
        Set("base_attack_damage_penetrates_elemental_resist_%", 3);
        Set("base_attack_damage_penetrates_chaos_resist_%", 30);
        var attack = Attack();
        Assert.Equal(25, attack.Fire);    // 10 general + 5 elemental + 7 attack fire + 3 attack elemental
        Assert.Equal(8, attack.Cold);     // 5 elemental + 3 attack elemental
        Assert.Equal(8, attack.Lightning);
        Assert.Equal(30, attack.Chaos);   // chaos has no general stat, only the attack-only one
        Assert.Equal(0, attack.Of(DamageType.Physical));   // physical never penetrates
        var spell = Spell();
        Assert.Equal(15, spell.Fire);     // 10 general + 5 elemental; no attack-only stats
        Assert.Equal(5, spell.Cold);      // the general elemental share
        Assert.Equal(5, spell.Lightning);
        Assert.Equal(0, spell.Chaos);
    }

    [Fact]
    public void The_weapon_hand_and_conditional_aliases_are_not_read()
    {
        // main_hand_/off_hand_ aliases, the local_ weapon scope, the non-critical conditional and the non-base
        // reduce_enemy variants stay out: with only those set, nothing penetrates.
        Set("main_hand_attack_damage_penetrates_fire_resist_%", 25);
        Set("local_fire_penetration_%", 25);
        Set("non_critical_strikes_penetrate_elemental_resistances_%", 25);
        Set("reduce_enemy_elemental_resistance_%", 25);
        Set("reduce_enemy_chaos_resistance_%", 25);
        Assert.Equal(default, Attack());
        Assert.Equal(default, Spell());
    }

    [Fact]
    public void Player_and_skill_penetration_on_the_same_stat_add_without_mutating_either_source()
    {
        Set("base_reduce_enemy_fire_resistance_%", 20);
        var skill = new Dictionary<uint, int>
        {
            [_catalog.Key("base_reduce_enemy_fire_resistance_%")!.Value] = 10,
            [_catalog.Key("base_penetrate_elemental_resistances_%")!.Value] = 5,
            [_catalog.Key("base_attack_damage_penetrates_fire_resist_%")!.Value] = 7,
        };
        Assert.Equal(42, Penetration.Of(_stats, _catalog, attack: true, skill).Fire);
        Assert.Equal(35, Penetration.Of(_stats, _catalog, attack: false, skill).Fire);
        Assert.Equal(20, _stats[_catalog.Key("base_reduce_enemy_fire_resistance_%")!.Value]);
        Assert.Equal(10, skill[_catalog.Key("base_reduce_enemy_fire_resistance_%")!.Value]);
    }

    [Fact]
    public void A_skill_prohibition_blocks_player_penetration_and_keeps_attack_chaos()
    {
        Set("base_reduce_enemy_fire_resistance_%", 20);
        Set("base_attack_damage_penetrates_chaos_resist_%", 30);
        var skill = new Dictionary<uint, int> { [_catalog.Key("cannot_penetrate_or_ignore_elemental_resistances")!.Value] = 1 };
        Assert.Equal(new Penetration.PerType(0, 0, 0, 30), Penetration.Of(_stats, _catalog, attack: true, skill));
        Assert.Equal(default, Penetration.Of(_stats, _catalog, attack: false, skill));
    }

    [Fact]
    public void The_cannot_penetrate_flag_turns_every_elemental_penetration_off_and_keeps_chaos()
    {
        Set("base_reduce_enemy_fire_resistance_%", 10);
        Set("base_penetrate_elemental_resistances_%", 5);
        Set("base_attack_damage_penetrates_fire_resist_%", 7);
        Set("base_attack_damage_penetrates_elemental_resist_%", 3);
        Set("base_attack_damage_penetrates_chaos_resist_%", 30);
        Set("cannot_penetrate_or_ignore_elemental_resistances", 1);
        var attack = Attack();
        Assert.Equal(default, new Penetration.PerType(attack.Fire, attack.Cold, attack.Lightning, 0));
        Assert.Equal(30, attack.Chaos);   // chaos is not elemental
        Assert.Equal(default, Spell());
    }

    [Fact]
    public void The_rows_the_mechanic_reads_are_the_expected_Stats_dat_rows()
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        var catalog = new StatCatalog(data);
        Assert.Equal(596u, catalog.Key("base_reduce_enemy_fire_resistance_%"));
        Assert.Equal(597u, catalog.Key("base_reduce_enemy_cold_resistance_%"));
        Assert.Equal(598u, catalog.Key("base_reduce_enemy_lightning_resistance_%"));
        Assert.Equal(3887u, catalog.Key("base_penetrate_elemental_resistances_%"));
        Assert.Equal(14473u, catalog.Key("base_attack_damage_penetrates_fire_resist_%"));
        Assert.Equal(14474u, catalog.Key("base_attack_damage_penetrates_cold_resist_%"));
        Assert.Equal(14475u, catalog.Key("base_attack_damage_penetrates_lightning_resist_%"));
        Assert.Equal(14476u, catalog.Key("base_attack_damage_penetrates_elemental_resist_%"));
        Assert.Equal(14479u, catalog.Key("base_attack_damage_penetrates_chaos_resist_%"));
        Assert.Equal(12052u, catalog.Key("cannot_penetrate_or_ignore_elemental_resistances"));
    }

    [Fact]
    public void A_real_passive_and_the_attack_only_stats_penetrate_the_map_boss_on_a_fixed_roll()
    {
        if (Data() is not { } data) return; // Real-client cross-check is optional on a fresh clone.
        var catalog = new MonsterCatalog(data);
        var stats = new StatCatalog(data);
        var mapBoss = catalog.Find("Metadata/Monsters/ZombieBoss/MapZombieBossHillock")!;
        var monster = new SpawnedMonster("map boss", new WalkEstimator(0, 0, 30), 60, 4, 100)
            { Variety = mapBoss, Level = 68 };
        // Ngamahu's Flame (base_reduce_enemy_fire_resistance_% = 20) plus an attack-only elemental share of 10
        // (Forces of Nature's stat): the attack's fire pen is 30, a spell's is 20.
        var player = new Dictionary<uint, int> { [stats.Key("base_reduce_enemy_fire_resistance_%")!.Value] = 20 };
        var attackStats = new Dictionary<uint, int>(player)
            { [stats.Key("base_attack_damage_penetrates_elemental_resist_%")!.Value] = 10 };
        var rolled = DamagePacket.Of(DamageType.Fire, 100, 100);
        Assert.Equal(90, Program.MonsterDamage(monster, rolled, new Random(11),
            Penetration.Of(attackStats, stats, attack: true)));    // 40% base - 30 pen = 10%
        Assert.Equal(80, Program.MonsterDamage(monster, rolled, new Random(11),
            Penetration.Of(player, stats, attack: false)));        // 40% base - 20 pen = 20%
        // Without penetration the same fixed roll stays at the base 60.
        Assert.Equal(60, Program.MonsterDamage(monster, rolled, new Random(11), default));
        // Chaos on the same monster (25%) with the attack-only chaos pen of 30 takes the effective value to -5%.
        Assert.Equal(105, Program.MonsterDamage(monster, DamagePacket.Of(DamageType.Chaos, 100, 100), new Random(11),
            new Penetration.PerType(0, 0, 0, 30)));
    }

    static string? Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RepositoryRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables")))
                return Path.Combine(dir.FullName, "data");
        return null;
    }
}
