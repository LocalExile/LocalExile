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
        // The ice and chaos storms share Firestorm's ActiveSkills row and its fire_storm_fireball_delay_ms, but
        // their damage is not fire, so they take no fall schedule.
        Assert.Equal("CannibalIcestorm", skills.EffectId(1383));
        Assert.Null(skills.MonsterStorm(1383, 1));
        Assert.Equal("DarkExileChaosstorm", skills.EffectId(1390));
        Assert.Null(skills.MonsterStorm(1390, 1));
    }

    // A skill with a measured row releases at its row x 33 ms from the start of its action; a skill without a row
    // keeps the clip contact's fraction of the attack time.
    [Fact]
    public void A_measured_skill_releases_at_its_launch_frame()
    {
        var skills = new SkillCatalog();
        Assert.Equal(14, skills.LaunchFrames("MonsterSpark"));
        Assert.Equal(5, skills.LaunchFrames("HeraldOfAgonyMinionTailSpike"));
        Assert.Null(skills.LaunchFrames("Firestorm"));
        Assert.Null(skills.LaunchFrames(null));
        var attack = new MonsterAttack(0, 1.0, 10, DamagePacket.Of(DamageType.Physical, 10, 20))
        {
            Ranged = true,
            LaunchFrames = skills.LaunchFrames("MonsterSpark"),
        };
        Assert.Equal(14 * 0.033, Game.MonsterLaunchDelay(attack, 0.375, 1.0), 10);
        // A skill without a row keeps the contact path.
        Assert.Equal(0.375, Game.MonsterLaunchDelay(attack with { LaunchFrames = null }, 0.375, 1.0), 10);
    }
}
