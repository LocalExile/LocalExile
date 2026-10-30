using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class HitReactionTests
{
    static SpawnedMonster Monster(uint maxLife = 100, double seconds = 0, params int[] flinches) =>
        new("Rhoa@1", new WalkEstimator(0, 0, 30, null, 2000, 2000), aggroRange: 60, stopRange: 3, maxLife: maxLife)
        {
            HitAnimations = [.. flinches.Select(i => new HitAnimation(i, seconds))],
        };

    static HitAnimation? Flinch(SpawnedMonster m, int damage, double now) => HitReaction.TryFlinch(m, damage, now, new Random(1));

    [Fact]
    public void A_hit_below_a_tenth_of_max_life_does_not_flinch()
    {
        var m = Monster(100, 0, 7);
        Assert.Null(Flinch(m, 9, now: 10));
        Assert.Equal(7, Flinch(m, 10, now: 10)?.Index);
    }

    [Fact]
    public void A_flinch_stops_the_current_walk_and_cancels_only_the_ordinary_swing_recovery()
    {
        var m = Monster(100, 0.8, 7);
        m.Walk.Order(100, 0, 0);
        m.LastTarget = (100, 0);
        m.ActionGeneration = 4;
        m.SwingGeneration = 4;
        m.BusyUntil = 20;
        var at = m.Walk.Position(1);

        Assert.NotNull(Flinch(m, 10, now: 1));

        Assert.Equal(at, m.Walk.Position(1));
        Assert.Equal(at, m.Walk.Position(10));
        Assert.Equal(at, m.CollisionPosition);
        Assert.Null(m.LastTarget);
        Assert.Equal(5, m.ActionGeneration);
        Assert.Null(m.SwingGeneration);
        Assert.Equal(1, m.BusyUntil);
        Assert.Equal(1.8, m.FlinchUntil, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_flinch_keeps_emerge_and_boss_stage_lifetimes(bool bossStage)
    {
        var m = Monster(100, 0.4, 7);
        m.StageRunning = bossStage;
        m.BusyUntil = bossStage ? 5 : double.PositiveInfinity;
        m.SwingGeneration = bossStage ? 0 : null;

        Assert.NotNull(Flinch(m, 10, now: 1));

        Assert.Equal(bossStage ? 5 : double.PositiveInfinity, m.BusyUntil);
        Assert.Equal(1.4, m.FlinchUntil, 6);
    }

    [Fact]
    public void Rejected_hits_do_not_stop_or_replace_the_monsters_action()
    {
        var m = Monster(100, 0.8, 7);
        m.Walk.Order(100, 0, 0);
        m.LastTarget = (100, 0);
        m.SwingGeneration = 0;
        m.BusyUntil = 5;

        Assert.Null(Flinch(m, 9, now: 1));
        Assert.Equal((60, 0), m.Walk.Position(2));
        Assert.Equal((100, 0), m.LastTarget);
        Assert.Equal(0, m.ActionGeneration);
        Assert.Equal(0, m.SwingGeneration);
        Assert.Equal(5, m.BusyUntil);

        Assert.NotNull(Flinch(m, 10, now: 2));
        int generation = m.ActionGeneration;
        m.LastTarget = (90, 0);
        Assert.Null(Flinch(m, 10, now: 2.2));
        Assert.Equal(generation, m.ActionGeneration);
        Assert.Equal((90, 0), m.LastTarget);
        Assert.Equal(2.8, m.FlinchUntil, 6);
    }
}
