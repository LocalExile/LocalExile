using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class UniqueFlaskTests
{
    // Dying Sun and Divination Distillate: "during effect" mods scale with increased flask effect, rounded down, and a
    // recovery flask's stats last exactly as long as that use's recovery.
    [Fact]
    public void During_effect_mods_scale_and_recovery_flask_stats_end_with_the_recovery()
    {
        var local = new Dictionary<string, int>
        {
            ["local_flask_number_of_additional_projectiles_during_flask_effect"] = 2,
            ["local_unique_flask_item_rarity_+%_while_healing"] = 45,
            ["local_unique_flask_resist_all_elements_%_during_flask_effect"] = 10,
        };
        var sources = UtilityFlasks.DuringEffectSources(local, 1.5);
        Assert.Contains(sources, s => s is { Stat: "number_of_additional_projectiles", Value: 3 });
        Assert.Contains(sources, s => s is { Stat: "base_item_found_rarity_+%", Value: 67 });
        Assert.Contains(sources, s => s is { Stat: "base_resist_all_elements_%", Value: 15 });

        var running = new Flasks.Effects();
        var recovery = new Flasks.Effect(0, 1, 121, 100, 28, 0, 5);
        running.Start(recovery);
        var stats = new UtilityFlasks.RecoveryDuringEffects();
        stats.Start([recovery], sources);
        Assert.Equal(3, stats.Sources(running).Count());
        // Full pools end the recovery early: the stats go with it, and only once.
        running.End(_ => true);
        Assert.Empty(stats.Sources(running));
        Assert.True(stats.Prune(running));
        Assert.False(stats.Prune(running));
    }

    // Bottled Faith's tripled ground and its damage taken, and Stormblood's crit-only ailments.
    [Fact]
    public void Bottled_Faith_ground_and_Stormblood_critical_ailments()
    {
        var local = new Dictionary<string, int>
        {
            ["local_consecrate_ground_on_flask_use_radius"] = 18,
            [FlaskGrounds.ConsecratedAreaStat] = 200,
        };
        Assert.Equal((FlaskGrounds.Kind.ConsecratedGround, 54), FlaskGrounds.OnUse(local).Single());
        var conn = new ClientState(new(1, "faith", 100, 100, 100, 100, null, []), 0, 1);
        conn.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, 10, 10, 54, 100, null, EnemyDamageTakenPct: 8));
        conn.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, 12, 12, 18, 100, null, EnemyDamageTakenPct: 5));
        Assert.Equal(8, conn.FlaskGrounds.ConsecratedEnemyDamageTaken((20, 20)));
        Assert.Equal(0, conn.FlaskGrounds.ConsecratedEnemyDamageTaken((200, 200)));

        var physical = DamagePacket.Of(DamageType.Physical, 1000, 1000);
        int Stormblood(string name) => name == "all_damage_from_critical_strikes_can_freeze" ? 1 : 0;
        bool Freezes(bool critical) => new MonsterElementalAilments().Inflict(physical, critical, threshold: 1000, now: 0,
            Stormblood, _ => 0, new Random(1)).Any(e => e.Kind == MonsterElementalAilments.Kind.Freeze);
        Assert.True(Freezes(critical: true));
        Assert.False(Freezes(critical: false));
    }
}
