using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Fire Fury's Firestorm (GrantedEffects 1463 FemaleCannibalBossFireStorm, stat set 1474) and the measured monster
// launch frames (SkillCatalog.LaunchFrames). A test never waits on real time: these read the decision, not a clock.
public sealed class MonsterStormsLaunchTests
{
    // 3000 / 100 from the skill's stat set, one fall each 0.1 s, each inside the skill's 10-cell drop area scaled by its
    // -74% storm area (10 x sqrt(0.26) = 5.1 cells). The fall's hit is the skill's own 3-4 fire damage.
    [Fact]
    public void A_monster_firestorm_schedules_one_fall_per_interval_for_its_duration()
    {
        var skills = new SkillCatalog();
        var storm = skills.MonsterStorm(1463, 1);
        Assert.NotNull(storm);
        Assert.Equal(3000, storm.DurationMs);
        Assert.Equal(100, storm.DelayMs);
        Assert.Equal(30, storm.Falls);
        Assert.Equal(0.1, storm.IntervalSeconds, 9);
        Assert.Equal(10 * Math.Sqrt(.26), storm.Radius, 6);
        Assert.Equal(10 * Math.Sqrt(.26), storm.ImpactRadius, 6);
        Assert.Equal(3, storm.Damage[DamageType.Fire].Min, 6);
        Assert.Equal(4, storm.Damage[DamageType.Fire].Max, 6);
        var falls = Game.StormFalls(storm, 5, (100, 100), owner: 0x3001, sequence: Game.MonsterActionSequence);
        Assert.Equal(30, falls.Length);
        for (int i = 0; i < falls.Length; i++)
        {
            Assert.Equal(5 + i * .1, falls[i].At, 9);
            double dx = falls[i].Cell.X - 100, dy = falls[i].Cell.Y - 100;
            // A cell rounding moves the centre at most half a cell diagonally past the drawn radius.
            Assert.True(Math.Sqrt(dx * dx + dy * dy) <= storm.Radius + .71, $"fall {i} at {falls[i].Cell}");
        }
        Assert.True(falls.Select(f => f.Cell).Distinct().Count() > 20);
        // Multi-level monster storm resolves by MonsterEffectLevel (Act 3 Gravicius level 28 -> gem level 1, not level 3)
        // and keeps its individual fireball blast radius (10) separate from its +600% drop zone radius.
        var gravicius = skills.MonsterStorm(1457, 28);
        Assert.NotNull(gravicius);
        Assert.Equal(128, gravicius.Damage[DamageType.Fire].Min, 6);
        Assert.Equal(192, gravicius.Damage[DamageType.Fire].Max, 6);
        Assert.Equal(10 * Math.Sqrt(7), gravicius.Radius, 6);
        Assert.Equal(10, gravicius.ImpactRadius, 6);
        // Non-FireStorm ActionTypes (EnchantmentMeteor / FireStormCascade) without a base radius return null.
        Assert.Null(skills.MonsterStorm(5442, 60));
        Assert.Null(skills.MonsterStorm(6160, 70));
        // The ice and chaos storms share Firestorm's ActiveSkills row and its fire_storm_fireball_delay_ms, but
        // their damage is not fire, so they take no fall schedule.
        Assert.Equal("CannibalIcestorm", skills.EffectId(1383));
        Assert.Null(skills.MonsterStorm(1383, 1));
        Assert.Equal("DarkExileChaosstorm", skills.EffectId(1390));
        Assert.Null(skills.MonsterStorm(1390, 1));
    }

    // A skill with a measured row releases at its row x 33 ms from the start of its action (scaled by runtime action
    // speed and clamped inside the action duration); a skill without a row keeps the clip contact's fraction.
    [Fact]
    public void A_measured_skill_releases_at_its_launch_frame()
    {
        var skills = new SkillCatalog();
        Assert.Equal(14, skills.LaunchFrames("MonsterSpark"));
        Assert.Equal(5, skills.LaunchFrames("HeraldOfAgonyMinionTailSpike"));
        Assert.Equal(0, skills.LaunchFrames("MonsterSplitArrow"));
        Assert.Null(skills.LaunchFrames("Firestorm"));
        Assert.Null(skills.LaunchFrames(null));
        var attack = new MonsterAttack(0, 1.0, 10, DamagePacket.Of(DamageType.Physical, 10, 20))
        {
            Ranged = true,
            LaunchFrames = skills.LaunchFrames("MonsterSpark"),
        };
        Assert.Equal(14 * 0.033, Game.MonsterLaunchDelay(attack, 0.375, 1.0), 10);
        // Runtime action-speed slow (e.g. 50% slower from chill) stretches the launch frame proportionally.
        Assert.Equal(14 * 0.033 * 1.5, Game.MonsterLaunchDelay(attack, 0.375, 1.5), 10);
        // Accelerated cast/attack shorter than the raw measured frame clamps strictly before the action ends.
        var fast = attack with { Seconds = 0.40 };
        Assert.Equal(0.40 - 0.033, Game.MonsterLaunchDelay(fast, 0.375, 0.40), 10);
        // Frame 0 releases immediately on the action start.
        Assert.Equal(0, Game.MonsterLaunchDelay(attack with { LaunchFrames = 0 }, 0.375, 1.0), 10);
        // A skill without a row keeps the contact path.
        Assert.Equal(0.375, Game.MonsterLaunchDelay(attack with { LaunchFrames = null }, 0.375, 1.0), 10);
    }
}
