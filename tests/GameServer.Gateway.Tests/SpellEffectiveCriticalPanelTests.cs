namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Fact]
    public void Selected_spell_publishes_its_effective_and_twice_critical_chance()
    {
        using var f = new Contact(new Draws(), "Fireball", "Rusted Sword");
        foreach (var (life, bifurcate) in new[] { (51u, false), (50u, false), (51u, true), (50u, true) })
        {
            f.Conn.PlayerLife = life;
            f.Set("spell_critical_strikes_bifurcate", bifurcate ? 1 : 0);
            var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            int Get(string name) => panel[Program.Stats.Key(name)!.Value];
            int chance = Get("spell_critical_strike_chance");
            int rolls = Get("virtual_extra_critical_rolls");
            Assert.InRange(chance, 100, 9900);
            Assert.Equal(life == 50u ? 1 : 0, rolls);
            Assert.Equal(CriticalStrikePolicy.EffectiveChance(chance, rolls, bifurcate),
                Get("spell_effective_total_chance_permyriad_for_hit_to_be_critical"));
            Assert.Equal(CriticalStrikePolicy.DoubleCriticalChance(chance, rolls, bifurcate),
                Get("spell_effective_total_chance_permyriad_for_hit_to_be_double_critical"));
            Assert.Equal(0, Get("skill_show_average_damage_instead_of_dps"));
        }
        f.Set("global_cannot_crit", 1);
        f.Refresh();
        var prohibited = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(0, prohibited[Program.Stats.Key("spell_effective_total_chance_permyriad_for_hit_to_be_critical")!.Value]);
        Assert.Equal(0, prohibited[Program.Stats.Key("spell_effective_total_chance_permyriad_for_hit_to_be_double_critical")!.Value]);
    }

    [Fact]
    public void Character_stats_carry_the_defence_estimates_and_the_pools_spell_effective_chance()
    {
        using var f = new Contact(new Draws(), null, "Rusted Sword");
        f.Conn.PlayerLife = 51;
        f.Conn.Pools = f.Conn.Pools with { Armour = 2000, Evasion = 800 };
        var stats = Program.CharacterPlayerStats(f.Conn);
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        int damage = Program.Monsters.BaseDamage(f.Conn.Pools.Level) ?? 0;
        int accuracy = Program.Monsters.BaseAccuracy(f.Conn.Pools.Level) ?? 0;
        Assert.True(damage > 0 && accuracy > 0);
        Assert.Equal(GameServer.Core.World.DamageModel.ArmourReduction(2000, damage), Get("display_estimated_physical_damage_reduciton_%"));
        Assert.Equal(GameServer.Core.World.DamageModel.EvadeChance(800, accuracy), Get("chance_to_evade_%"));
        Assert.Equal(CriticalStrikePolicy.EffectiveChance(f.Conn.Pools.SpellCriticalStrikeChance),
            Get("spell_effective_total_chance_permyriad_for_hit_to_be_critical"));
        Assert.Equal(0, Get("spell_effective_total_chance_permyriad_for_hit_to_be_double_critical"));
        f.Set("spell_critical_strikes_bifurcate", 1);
        stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(CriticalStrikePolicy.DoubleCriticalChance(f.Conn.Pools.SpellCriticalStrikeChance, 0, true),
            Get("spell_effective_total_chance_permyriad_for_hit_to_be_double_critical"));
    }
}
