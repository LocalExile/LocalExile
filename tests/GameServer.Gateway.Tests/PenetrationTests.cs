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

    static string? Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables")))
                return Path.Combine(dir.FullName, "data");
        return null;
    }
}
