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

        CinderswallowRetainsOnlyTheQualifyingIgnite();

        var player = new Dictionary<uint, int>();
        void Set(string name, int value) => player[Program.Stats.Key(name)!.Value] = value;
        Set("damage_+%", 100);
        Set("area_damage_+%", 50);
        Set("elemental_damage_+%", 50);
        Set("attack_damage_+%", 900);
        Set("spell_damage_+%", 900);
        Assert.Equal(30, Game.OriathExplosionPacket(100, DamageType.Fire, player)[DamageType.Fire].Min);
        Set("fire_damage_%_to_add_as_chaos", 20);
        Assert.Equal(6, Game.OriathExplosionPacket(100, DamageType.Fire, player)[DamageType.Chaos].Min);
        Set("base_deal_no_fire_damage", 1);
        Assert.Equal(0, Game.OriathExplosionPacket(100, DamageType.Fire, player)[DamageType.Fire].Min);
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

        // Increased critical chance against Blinded enemies adds to Bottled Faith and Profane Ground.
        var target = new SpawnedMonster("blinded", new(10, 10, 0), 0, 0, 100);
        conn.Monsters[7] = target;
        conn.FlaskGrounds.SetBlinded(7, true, () => 99);
        StartFlask(conn, FlaskGrounds.VsConsecratedCriticalStat, 100);
        var critical = Game.SpellCriticalChanceVsGround(conn, extra => 500 + (int)extra * 5, blinded: 200);
        Assert.Equal(2000, Game.CriticalChanceOn(conn, 7, 500, 1500, critical));
        conn.FlaskGrounds.SetProfane(7, true, true, 1, () => 100);
        Assert.Equal(2500, Game.CriticalChanceOn(conn, 7, 500, 1500, critical));
    }

    static void StartFlask(ClientState conn, string stat, int value) =>
        conn.UtilityFlaskEffects.Start(new(1, 0, "test", new(0, 0, "test", [], 10, 1, 10), 1, null,
            [new("mod " + stat, stat, value)], 0, 10));

    static void CinderswallowRetainsOnlyTheQualifyingIgnite()
    {
        var conn = new ClientState(new(1, "cinderswallow", 100, 100, 100, 100, null, []), 0, 1);
        var target = new SpawnedMonster("target", new(10, 10, 0), 0, 0, 10000);
        // A long, earlier ignite cannot extend the shorter ignite inflicted during flask effect.
        Game.IgniteLocked(conn, 1, target, new(DamageType.Fire, 1, 100), 0);
        StartFlask(conn, UtilityFlasks.IgnitedDuringEffectTakenStat, 20);
        Game.IgniteLocked(conn, 1, target, new(DamageType.Fire, 1, 4), 1);
        Assert.Equal(1.2, Game.MonsterDamageTaken(target, false, 2), 6);
        Assert.Equal(1, Game.MonsterDamageTaken(target, false, 5), 6);
        // A weaker flask's longer ignite must not retain the stronger expired modifier.
        StartFlask(conn, UtilityFlasks.IgnitedDuringEffectTakenStat, 7);
        Game.IgniteOnHitLocked(conn, 1, target, new(100, 1, 1, 1, 1, 10), false, 2);
        Assert.Equal(1.07, Game.MonsterDamageTaken(target, false, 6), 6);
        target.Ignites.SetExpiryRate(6, .5);
        Assert.Equal(1.07, Game.MonsterDamageTaken(target, false, 17), 6);
        Assert.Equal(1, Game.MonsterDamageTaken(target, false, 18), 6);

        // Dying ignited is a historical kill flag, not an indefinitely ignited corpse.
        var expired = new SpawnedMonster("old corpse", new(10, 10, 0), 0, 0, 100);
        expired.Ignites.Add(-100, 1, 4);
        expired.Ignites.Clear();
        Assert.True(expired.Ignites.DiedBurning);
        Game.NoteCorpseConsumedLocked(conn, expired);
        Assert.Equal(0, conn.IgnitedCorpsesConsumed);
        var fresh = new SpawnedMonster("fresh corpse", new(10, 10, 0), 0, 0, 100);
        fresh.Ignites.Add(0, 1, 1000);
        fresh.Ignites.Clear();
        Game.NoteCorpseConsumedLocked(conn, fresh);
        Assert.Equal(1, conn.IgnitedCorpsesConsumed);
    }
}
