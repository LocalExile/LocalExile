using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class UniqueFlaskTests
{
    // Dying Sun and Divination Distillate: "during effect" mods scale with increased flask effect, rounded down, and a
    // recovery flask's stats last exactly as long as that use's recovery.
    [Fact]
    public void During_effect_mods_scale_and_recovery_flask_stats_end_with_the_recovery()
    {
        var local = new Dictionary<uint, int>
        {
            [Stat.LocalFlaskNumberOfAdditionalProjectilesDuringFlaskEffect] = 2,
            [Stat.LocalUniqueFlaskItemRarityPlusPctWhileHealing] = 45,
            [Stat.LocalUniqueFlaskResistAllElementsPctDuringFlaskEffect] = 10,
        };
        var sources = UtilityFlasks.DuringEffectSources(local, 1.5);
        Assert.Contains(sources, s => s is { Stat: Stat.NumberOfAdditionalProjectiles, Value: 3 });
        Assert.Contains(sources, s => s is { Stat: Stat.BaseItemFoundRarityPlusPct, Value: 67 });
        Assert.Contains(sources, s => s is { Stat: Stat.BaseResistAllElementsPct, Value: 15 });

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
        void Set(uint name, int value) => player[name] = value;
        Set(Stat.DamagePlusPct, 100);
        Set(Stat.AreaDamagePlusPct, 50);
        Set(Stat.ElementalDamagePlusPct, 50);
        Set(Stat.AttackDamagePlusPct, 900);
        Set(Stat.SpellDamagePlusPct, 900);
        Assert.Equal(30, Game.OriathExplosionPacket(100, DamageType.Fire, player)[DamageType.Fire].Min);
        Set(Stat.FireDamagePctToAddAsChaos, 20);
        Assert.Equal(6, Game.OriathExplosionPacket(100, DamageType.Fire, player)[DamageType.Chaos].Min);
        Set(Stat.BaseDealNoFireDamage, 1);
        Assert.Equal(0, Game.OriathExplosionPacket(100, DamageType.Fire, player)[DamageType.Fire].Min);
    }

    // The conditional-immunity flask suffixes remove their ailment and protect for their rolled seconds ONLY when
    // used while afflicted (never leaking unconditional immunity into DuringEffect / Mageblood), and Aquamarine
    // Flask's chilled ground resolves its GroundEffects Ice art, marks NeedsMonsterUpdates, and creates a radius-28 ground.
    [Fact]
    public void Flask_conditional_immunities_and_chilled_ground()
    {
        var local = new Dictionary<uint, int>
        {
            [Stat.LocalFlaskChillOrFreezeImmunityIfChilledOrFrozenS] = 8,
            [Stat.LocalFlaskIgniteImmunityIfIgnitedAndRemoveBurningS] = 6,
            [Stat.LocalFlaskShockImmunityIfShockedS] = 5,
        };
        // Conditional "if used while ..." suffixes must not grant unconditional DuringEffect stats (e.g. Mageblood).
        Assert.Empty(UtilityFlasks.DuringEffectSources(local, 1.5));

        var player = new Player(new(1, "cond-flask", 100, 100, 100, 100, null, []), 0, 1);
        // Unafflicted use grants no protection.
        Game.ApplyConditionalFlaskAilmentProtectionLocked(player, local, now: 1);
        Assert.False(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Chill, 2));
        Assert.False(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Freeze, 2));
        Assert.False(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Ignite, 2));
        Assert.False(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Shock, 2));

        // Afflicted use removes each active ailment and protects for its exact rolled duration.
        player.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Freeze, 100, 2, now: 1, _ => 0, new Random(1));
        player.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite, 50, 4, now: 1, _ => 0, new Random(1));
        player.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Shock, 20, 2, now: 1, _ => 0, new Random(1));
        Game.ApplyConditionalFlaskAilmentProtectionLocked(player, local, now: 1);
        Assert.Equal((false, 0, false, 0), player.ElementalAilments.Snapshot(1));
        Assert.True(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Chill, 8.5));
        Assert.True(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Freeze, 8.5));
        Assert.False(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Freeze, 9.5));
        Assert.True(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Ignite, 6.5));
        Assert.False(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Ignite, 7.5));
        Assert.True(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Shock, 5.5));
        Assert.False(player.ElementalAilments.IsProtected(PlayerElementalAilments.Kind.Shock, 6.5));
        // New applications while protected are blocked.
        player.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Shock, 25, 2, now: 3, _ => 0, new Random(1));
        Assert.Equal(0, player.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Shock, 3));

        Assert.Equal((FlaskGrounds.Kind.ChilledGround, 28), FlaskGrounds.OnUse(
            new Dictionary<uint, int> { [Stat.LocalFlaskChilledGroundOnFlaskUseRadius] = 28 }).Single());
        Assert.True(FlaskGrounds.LoadArt("data").ContainsKey(FlaskGrounds.Kind.ChilledGround));
        var grounds = new FlaskGrounds.State();
        grounds.Add(new(FlaskGrounds.Kind.ChilledGround, 10, 10, 28, 10));
        Assert.True(grounds.NeedsMonsterUpdates);
    }

    // A use's "during effect" suffix buffs: one BuffDefinitions row per suffix mod, in DuringEffect order
    // (flask_bonus_cast_speed 1386, flask_bonus_attack_speed 1385, flask_bonus_mana_cost 1241); a mod with no
    // flask_bonus_* row sends nothing.
    [Fact]
    public void Flask_suffix_buffs_are_one_row_per_suffix()
    {
        var local = new Dictionary<uint, int>
        {
            [Stat.LocalFlaskCastSpeedPlusPctWhileHealing] = 20,
            [Stat.LocalFlaskAttackSpeedPlusPctWhileHealing] = 15,
            [Stat.LocalFlaskSkillManaCostPlusPctDuringFlaskEffect] = -14,
            [Stat.LocalFlaskMovementSpeedPlusPctWhileHealing] = 10,
        };
        var rows = new Dictionary<uint, ushort>
        {
            [Stat.BaseCastSpeedPlusPct] = 1386,
            [Stat.AttackSpeedPlusPct] = 1385,
            [Stat.BaseManaCostMinusPct] = 1241,
        };
        Assert.Equal(new ushort[] { 1386, 1385, 1241 }, UtilityFlasks.SuffixBuffRowsFor(local, rows));
    }

    // Bottled Faith's tripled ground and its damage taken, and Stormblood's crit-only ailments.
    [Fact]
    public void Bottled_Faith_ground_and_Stormblood_critical_ailments()
    {
        var local = new Dictionary<uint, int>
        {
            [Stat.LocalConsecrateGroundOnFlaskUseRadius] = 18,
            [FlaskGrounds.ConsecratedAreaStat] = 200,
        };
        Assert.Equal((FlaskGrounds.Kind.ConsecratedGround, 54), FlaskGrounds.OnUse(local).Single());
        var player = new Player(new(1, "faith", 100, 100, 100, 100, null, []), 0, 1);
        player.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, 10, 10, 54, 100, null, EnemyDamageTakenPct: 8));
        player.FlaskGrounds.Add(new(FlaskGrounds.Kind.ConsecratedGround, 12, 12, 18, 100, null, EnemyDamageTakenPct: 5));
        Assert.Equal(8, player.FlaskGrounds.ConsecratedEnemyDamageTaken((20, 20)));
        Assert.Equal(0, player.FlaskGrounds.ConsecratedEnemyDamageTaken((200, 200)));

        var physical = DamagePacket.Of(DamageType.Physical, 1000, 1000);
        int Stormblood(uint name) => name == Stat.AllDamageFromCriticalStrikesCanFreeze ? 1 : 0;
        bool Freezes(bool critical) => new MonsterElementalAilments().Inflict(physical, critical, threshold: 1000, now: 0,
            Stormblood, _ => 0, new Random(1)).Any(e => e.Kind == MonsterElementalAilments.Kind.Freeze);
        Assert.True(Freezes(critical: true));
        Assert.False(Freezes(critical: false));

        // Increased critical chance against Blinded enemies adds to Bottled Faith and Profane Ground.
        var target = new SpawnedMonster("blinded", new(10, 10, 0), 0, 0, 100) { Hostile = true };
        player.Area.Monsters[7] = target;
        player.FlaskGrounds.SetBlinded(7, true, () => 99);
        // The grounds are zones of the area (AreaZones), which a player outside a socket does not have: it gets one, the
        // grounds go in, and the area's movement places the target inside them.
        var area = player.Area;
        typeof(Game).GetMethod("SyncFlaskGroundZones", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [player]);
        area.Grid.Set(new(7), (10, 10));
        area.Zones.Update(moved: true);
        StartFlask(player, FlaskGrounds.VsConsecratedCriticalStat, 100);
        var critical = Game.SpellCriticalChanceVsGround(player, extra => 500 + (int)extra * 5, blinded: 200);
        Assert.Equal(2000, Game.CriticalChanceOn(player, 7, 500, 1500, critical));
        player.FlaskGrounds.SetProfane(7, true, true, 1, () => 100);
        Assert.Equal(2500, Game.CriticalChanceOn(player, 7, 500, 1500, critical));
    }

    static void StartFlask(Player player, uint stat, int value) =>
        player.UtilityFlaskEffects.Start(new(1, 0, "test", new(0, 0, "test", [], 10, 1, 10), 1, null,
            [new("mod " + Program.Stats.Name(stat), stat, value)], 0, 10));

    static void CinderswallowRetainsOnlyTheQualifyingIgnite()
    {
        var player = new Player(new(1, "cinderswallow", 100, 100, 100, 100, null, []), 0, 1);
        var target = new SpawnedMonster("target", new(10, 10, 0), 0, 0, 10000);
        // A long, earlier ignite cannot extend the shorter ignite inflicted during flask effect.
        Game.IgniteLocked(player, 1, target, new(DamageType.Fire, 1, 100), 0);
        StartFlask(player, UtilityFlasks.IgnitedDuringEffectTakenStat, 20);
        Game.IgniteLocked(player, 1, target, new(DamageType.Fire, 1, 4), 1);
        Assert.Equal(1.2, Game.MonsterDamageTaken(target, false, 2), 6);
        Assert.Equal(1, Game.MonsterDamageTaken(target, false, 5), 6);
        // A weaker flask's longer ignite must not retain the stronger expired modifier.
        StartFlask(player, UtilityFlasks.IgnitedDuringEffectTakenStat, 7);
        Game.IgniteOnHitLocked(player, 1, target, new(100, 1, 1, 1, 1, 10), false, 2);
        Assert.Equal(1.07, Game.MonsterDamageTaken(target, false, 6), 6);
        target.Ignites.SetExpiryRate(6, .5);
        Assert.Equal(1.07, Game.MonsterDamageTaken(target, false, 17), 6);
        Assert.Equal(1, Game.MonsterDamageTaken(target, false, 18), 6);

        // Dying ignited is a historical kill flag, not an indefinitely ignited corpse.
        var expired = new SpawnedMonster("old corpse", new(10, 10, 0), 0, 0, 100);
        expired.Ignites.Add(-100, 1, 4);
        expired.Ignites.Clear();
        Assert.True(expired.Ignites.DiedBurning);
        Game.NoteCorpseConsumedLocked(player, expired);
        Assert.Equal(0, player.IgnitedCorpsesConsumed);
        var fresh = new SpawnedMonster("fresh corpse", new(10, 10, 0), 0, 0, 100);
        fresh.Ignites.Add(0, 1, 1000);
        fresh.Ignites.Clear();
        Game.NoteCorpseConsumedLocked(player, fresh);
        Assert.Equal(1, player.IgnitedCorpsesConsumed);
    }
}
