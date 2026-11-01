using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class HitAndKillRecoveryTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name)!.Value, s => s.Value);

    static readonly Dictionary<string, int> GainRaw = new()
    {
        ["base_life_gain_per_target"] = 44, ["mana_gain_per_target"] = 3, ["energy_shield_gain_per_target"] = 2,
    };
    static readonly Dictionary<string, int> GainWeapon = new()
    {
        ["local_life_gain_per_target"] = 5, ["local_mana_gain_per_target"] = 1, ["local_life_and_mana_gain_per_target"] = 10,
    };

    // Culling Strike, gain on hit and Life Recoup: what each source gives, and the hit that applies it.
    [Fact]
    public void Hits_cull_gain_and_recoup_by_their_stats()
    {
        // Culling Strike: the threshold comes from the character and the skill.
        Assert.Equal(default, Game.Cull.Of(null, null, critical: true, spell: true));
        // The support: its implicit stat and its quality's life recovery.
        Assert.Equal(new Game.Cull(10, 2), Game.Cull.Of(null,
            With(("kill_enemy_on_hit_if_under_10%_life", 1), ("recover_%_maximum_life_on_cull", 2)), false, false));
        var crits = With(("crits_have_culling_strike", 1));
        Assert.Equal(0, Game.Cull.Of(crits, null, critical: false, spell: false).Threshold);
        Assert.Equal(10, Game.Cull.Of(crits, null, critical: true, spell: false).Threshold);
        var spells = With(("spells_have_culling_strike", 1));
        Assert.Equal(0, Game.Cull.Of(spells, null, critical: false, spell: false).Threshold);
        Assert.Equal(10, Game.Cull.Of(spells, null, critical: false, spell: true).Threshold);
        Assert.Equal(20, Game.Cull.Of(With(("kill_enemy_on_hit_if_under_20%_life", 1)),
            With(("kill_enemy_on_hit_if_under_10%_life", 1)), false, false).Threshold);

        // A hit that leaves the enemy at the threshold kills it.
        {
            var conn = new ClientState(new(1, "cull", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500,
            };
            var cull = new Game.Cull(10, 2);
            SpawnedMonster Monster(MonsterModSet? mods = null) => new("cull target", new(100, 100, 0), 0, 0, 1000)
                { Mods = mods ?? MonsterModSet.None };

            var above = Monster();
            Assert.Equal(101u, Game.TakeOwnerHitLocked(conn, above, 899, cull).Life);
            Assert.Equal(500u, conn.PlayerLife);

            var at = Monster();
            Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, at, 900, cull).Life);
            Assert.Equal(520u, conn.PlayerLife);   // 2% of 1000 maximum Life

            var immune = Monster(new([], [], new(new Dictionary<string, int> { ["immune_to_culling_strike"] = 1 })));
            Assert.Equal(100u, Game.TakeOwnerHitLocked(conn, immune, 900, cull).Life);
            Assert.Equal(100u, Game.TakeOwnerHitLocked(conn, Monster(), 900, default).Life);
        }

        // Gain per enemy hit comes from attacks and their weapon.
        Assert.Equal((59, 14, 2), LeechSources.From(GainRaw.GetValueOrDefault, attack: true, GainWeapon.GetValueOrDefault).GainOnHit);
        Assert.Equal((44, 3, 2), LeechSources.From(GainRaw.GetValueOrDefault, attack: true).GainOnHit);
        Assert.Equal(default, LeechSources.From(GainRaw.GetValueOrDefault, attack: false, GainWeapon.GetValueOrDefault).GainOnHit);

        // Every enemy hit grants its gain at once.
        {
            var conn = new ClientState(new(1, "gain", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 900, PlayerMana = 50,
            };
            var sources = LeechSources.From(GainRaw.GetValueOrDefault, attack: true, GainWeapon.GetValueOrDefault);
            var first = new SpawnedMonster("first", new(100, 100, 0), 0, 0, 100);
            var second = new SpawnedMonster("second", new(100, 100, 0), 0, 0, 100);
            // A hit that deals no damage still hits: the gain does not scale with damage.
            Game.LeechOnHitLocked(conn, first, DamagePacket.Empty, 0, sources);
            Game.LeechOnHitLocked(conn, second, DamagePacket.Empty, 0, sources);
            Assert.Equal(1000u, conn.PlayerLife);   // 900 + 2 × 59, capped at maximum Life
            Assert.Equal(78u, conn.PlayerMana);
            Assert.True(Game.LeechOf(conn).InstantLifeChanged && Game.LeechOf(conn).InstantManaChanged);
            Game.LeechOnHitLocked(conn, new("dead", new(100, 100, 0), 0, 0, 100) { Dead = true }, DamagePacket.Empty, 0, sources);
            Assert.Equal(78u, conn.PlayerMana);
        }

        // Life Recoup: a share of each hit's damage taken, raised by the Life recovery rate, recovered evenly over the
        // 4 s after the hit (3 s with Nine Lives); overlapping hits keep their own ends.
        {
            static Func<string, int> Get(params (string Name, int Value)[] stats) =>
                name => stats.FirstOrDefault(s => s.Name == name).Value;
            var hit = DamagePacket.Of(DamageType.Fire, 100, 100).Add(DamageType.Physical, new(50, 50));
            const string All = "damage_taken_goes_to_life_over_4_seconds_%";
            Assert.Equal(40, Game.LifeRecoupOf(Get((All, 10), ("fire_damage_taken_goes_to_life_over_4_seconds_%", 20),
                ("physical_damage_taken_recouped_as_life_%", 10)), hit), 9);
            Assert.Equal(0, Game.LifeRecoupOf(Get(), hit), 9);
            Assert.Equal(22.5, Game.LifeRecoupOf(Get((All, 10), ("life_recovery_rate_+%", 50)), hit), 9);
            Assert.Equal(0, Game.LifeRecoupOf(Get((All, 10), ("cannot_recover_life", 1)), hit), 9);
            Assert.Equal(4, Game.LifeRecoupSeconds(Get()));
            Assert.Equal(3, Game.LifeRecoupSeconds(Get(("recoup_effects_apply_over_3_seconds_instead", 1))));
            Assert.Equal(3, Game.LifeRecoupSeconds(Get(("recoup_life_effects_apply_over_3_seconds_instead", 1))));

            var life = new RecoupRecovery();
            life.Add(10, 40, 4);
            life.Add(12, 20, 4);
            Assert.Equal(10, life.Take(11), 9);
            Assert.Equal(25, life.Take(13), 9);
            Assert.Equal(25, life.Take(20), 9);
            Assert.Equal(0, life.Take(21), 9);
            life.Add(30, 8, 4);
            Assert.Equal(0, life.Take(29), 9);
            Assert.Equal(8, life.Take(34), 9);
        }
    }

    // Recovery on kill (with the corpse's ailments) and Onslaught on kill: what each source gives, and the kill that
    // applies it.
    [Fact]
    public void Kills_recover_and_grant_onslaught_by_their_stats()
    {
        // Kill recovery comes from flat, per-level and percent stats, and from the corpse's ailments.
        var raw = new Dictionary<string, int>
        {
            ["base_life_gained_on_enemy_death"] = 10, ["life_gained_on_enemy_death_per_level"] = 1,
            ["recover_%_maximum_life_on_kill"] = 3, ["base_mana_gained_on_enemy_death"] = 4,
            ["recover_%_maximum_mana_on_kill"] = 2, ["maximum_mana_%_gained_on_kill"] = 1,
            ["base_energy_shield_gained_on_enemy_death"] = 7, ["recover_energy_shield_%_on_kill"] = 5,
            ["life_gained_on_killing_ignited_enemies"] = 20, ["recover_%_maximum_life_on_killing_chilled_enemy"] = 1,
            ["gain_X_energy_shield_on_killing_shocked_enemy"] = 9, ["recover_X_mana_on_killing_frozen_enemy"] = 6,
        };
        var k = KillRecovery.From(raw.GetValueOrDefault, level: 20);
        Assert.Equal(new KillRecovery(30, 3, 4, 3, 7, 5), k.For(false, false, false, false));
        Assert.Equal(new KillRecovery(50, 4, 10, 3, 16, 5), k.For(true, true, true, true));
        Assert.Equal(new KillRecovery(30, 3, 10, 3, 7, 5), k.For(false, false, false, frozen: true));
        raw["cannot_recover_life"] = 1;
        raw["cannot_recover_energy_shield"] = 1;
        Assert.Equal(new KillRecovery(0, 0, 10, 3, 0, 0), KillRecovery.From(raw.GetValueOrDefault, level: 20).For(true, true, true, true));

        // A kill recovers at once by the corpse's ailments, and a dead player gains nothing.
        {
            var conn = new ClientState(new(1, "kill", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(10, 1000, 200, 2, 0), PlayerLife = 500, PlayerMana = 50,
            };
            Game.LeechOf(conn).OnKill = new KillRecovery(10, 2, 5, 3, 0, 0) { When = new(IgnitedLife: 7, 0, 0, FrozenMana: 4) };
            var corpse = new SpawnedMonster("kill", new(100, 100, 0), 0, 0, 100);
            Game.RecoverOnKill(conn, corpse);
            Assert.Equal(530u, conn.PlayerLife);   // 10 + 2% of 1000
            Assert.Equal(61u, conn.PlayerMana);    // floor(5 + 3% of 200); it was not frozen
            Assert.True(Game.LeechOf(conn).InstantLifeChanged && Game.LeechOf(conn).InstantManaChanged);
            corpse.Ailments.ShockOnKill(0, _ => 0, _ => 0);   // Within the Tempest shocks the corpse after the death
            Assert.True(corpse.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 1) > 0);
            Assert.False(corpse.Ailments.ShockedAtDeath(1));
            corpse.Ailments.Shatter();             // a shattered kill was of a frozen enemy
            corpse.Ignites.Clear(burnt: true);     // and its ignite's last tick killed it
            Game.RecoverOnKill(conn, corpse);
            Assert.Equal(567u, conn.PlayerLife);   // 530 + 10 + 7 + 2% of 1000
            Assert.Equal(76u, conn.PlayerMana);    // floor(5 + 4 + 3% of 200)
            conn.PlayerLife = 0;
            Game.RecoverOnKill(conn, corpse);
            Assert.Equal(0u, conn.PlayerLife);
            Assert.Equal(76u, conn.PlayerMana);
        }

        // Each Onslaught-on-kill stat rolls its own duration, and a gain never shortens a longer one.
        {
            var rolls = new Dictionary<string, int>
            {
                ["chance_to_gain_onslaught_on_kill_%"] = 10, ["chance_to_gain_onslaught_on_kill_for_10_seconds_%"] = 100,
            };
            Assert.Equal([(10, 4.0), (100, 10.0)], KillOnslaught.From(rolls.GetValueOrDefault));
            rolls["onslaught_time_granted_on_kill_ms"] = 6000;
            rolls["onslaught_buff_duration_on_kill_ms"] = 8000;       // Maraketh: always, for 8 s
            Assert.Equal([(10, 6.0), (100, 8.0), (100, 10.0)], KillOnslaught.From(rolls.GetValueOrDefault));

            var o = new KillOnslaught { Rolls = [(100, 4.0)] };
            Assert.True(o.Kill(0, new Random(1)));
            Assert.Equal(4, o.Until);
            Assert.True(o.Kill(3, new Random(1)));       // 1 s left: a new 4 s gain lengthens it
            Assert.Equal(7, o.Until);
            o.Rolls = [(100, 4.0)];
            o.Until = 20;
            Assert.False(o.Kill(10, new Random(1)));     // 10 s left: a 4 s gain does not shorten it
            Assert.Equal(20, o.Until);
            Assert.False(new KillOnslaught { Rolls = [] }.Kill(0, new Random(1)));
        }

        // A kill's Onslaught applies once the client is told, and counts once with a flask's.
        {
            var conn = new ClientState(new(1, "onslaught", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(10, 1000, 200, 2, 0), PlayerLife = 500,
            };
            Game.KillOnslaughtOf(conn).Rolls = [(100, 4.0)];
            Assert.True(Game.OnslaughtOnKill(conn));
            Assert.Empty(Game.KillOnslaughtSources(conn, _ => 0));    // not until the client has the buff
            var o = Game.KillOnslaughtOf(conn);
            double now = o.Until - 4;                                // granted for 4 s at the kill
            Assert.Equal(((ushort)0, (double?)4, true), o.Present(now, alive: true, hidden: false));
            o.Buff = 9;
            Assert.Null(o.Present(now + 1, alive: true, hidden: false));               // nothing new to send
            Assert.Equal(((ushort)9, (double?)null, false), o.Present(now + 1, alive: true, hidden: true)); // a flask's shows
            var kill = Game.KillOnslaughtSources(conn, name => name == "onslaught_effect_+%" ? 50 : 0).ToList();
            Assert.Equal(["attack_speed_+%", "base_cast_speed_+%", "base_movement_velocity_+%"], kill.Select(s => s.Stat));
            Assert.All(kill, s => Assert.Equal(30, s.Value));      // 20% at 50% increased effect
            // A Silver Flask's Onslaught at 80% more flask effect is the same buff: the stronger counts, once.
            var flask = UtilityFlasks.OnslaughtSources(Program.Items.Onslaught!, 1.8);
            Assert.All(UtilityFlasks.Strongest(kill.Concat(flask)), s => Assert.Equal(36, s.Value));
            Assert.Equal(3, UtilityFlasks.Strongest(kill.Concat(flask)).Count);
            Assert.Equal(((ushort)0, (double?)null, true), o.Present(now + 2, alive: false, hidden: true)); // death ends it
            Assert.False(o.Applied);
            Assert.Empty(Game.KillOnslaughtSources(conn, _ => 0));
        }
    }
}
