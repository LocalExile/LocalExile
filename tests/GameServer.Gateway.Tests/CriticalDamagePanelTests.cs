using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData(null, "Rusted Sword")]
    [InlineData(null, "Crude Bow")]
    [InlineData("Cyclone", "Rusted Sword")]
    [InlineData("Fireball", "Rusted Sword")]
    public void Damage_average_and_DPS_follow_current_luck_and_the_actual_action_duration(string? gem, string weapon)
    {
        using var f = new Contact(new Draws(), gem, weapon);
        var main = (SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [f.Conn])!;
        var hit = gem is null ? Program.AttackHitConverted(main, Program.CharacterPlayerStats(f.Conn), weapon != "Crude Bow", null) : f.Hit;
        double seconds = gem is null ? main.AttackSeconds(Program.CharacterPlayerStats(f.Conn))
            : gem == "Fireball" ? (int)Method("CastMs").Invoke(null, [f.Conn, Program.Skills.EffectRow(gem), f.Skill])! / 1000.0
            : (double)Method("SupportedAttackSeconds").Invoke(null, [f.Conn, f.Skill])!;
        foreach (var (life, extra, resultingRolls) in new[] { (51u, 0, 0), (50u, 0, 1), (51u, -1, -1), (50u, -1, 0) })
        {
            f.Conn.PlayerLife = life;
            f.Set("extra_critical_rolls", extra);
            var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            int Get(string name) => panel[Program.Stats.Key(name)!.Value];
            double chance = Get(gem == "Fireball" ? "spell_critical_strike_chance" : "main_hand_critical_strike_chance") / 10000.0;
            Assert.InRange(chance, .01, .99);
            Assert.Equal(resultingRolls, Get("virtual_extra_critical_rolls"));
            double criticalProbability = resultingRolls == 0 ? chance : resultingRolls > 0 ? 2 * chance - chance * chance : chance * chance;
            double average = hit.AverageTotal * (1 + criticalProbability * (f.Multiplier / 100.0 - 1));
            Assert.Equal((int)Math.Round(average * 100), Get("display_hundred_times_damage_per_skill_use"));
            Assert.Equal((int)Math.Round(average * 100 / seconds), Get("hundred_times_damage_per_second"));
            Assert.Equal((int)Math.Floor(hit.MaxTotal), Get("main_hand_maximum_total_damage"));
        }
    }

    [Theory]
    [InlineData(null, "Rusted Sword")]
    [InlineData(null, "Crude Bow")]
    [InlineData("Cyclone", "Rusted Sword")]
    [InlineData("PoisonArrow", "Crude Bow")]
    [InlineData("Fireball", "Rusted Sword")]
    [InlineData("IceNova", "Rusted Sword")]
    public void Published_damage_average_weights_critical_hits_and_clears_the_bonus_under_prohibition(string? gem, string weapon)
    {
        using var f = new Contact(new Draws(), gem, weapon);
        f.Set("critical_strike_chance_+%", 100000);
        f.Refresh();
        var hit = gem is null
            ? Program.AttackHitConverted((SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [f.Conn])!,
                Program.CharacterPlayerStats(f.Conn), weapon != "Crude Bow", null)
            : f.Hit;
        foreach (bool cannot in new[] { false, true, false })
        {
            f.Set("global_cannot_crit", cannot ? 1 : 0);
            f.Refresh();
            var panel = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill).Stats!.ToDictionary(s => s.Key, s => s.Value);
            Assert.Equal((int)Math.Round(100 * hit.AverageTotal * (cannot ? 1 : f.Multiplier / 100.0)),
                panel[Program.Stats.Key("display_hundred_times_damage_per_skill_use")!.Value]);
        }
    }
}
