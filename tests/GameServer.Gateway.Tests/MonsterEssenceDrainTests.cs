namespace GameServer.Gateway.Tests;

public sealed class MonsterEssenceDrainTests
{
    [Fact]
    public void Essence_drain_strongest_instance_ticks_leeches_and_expires()
    {
        var drain = new MonsterEssenceDrain();
        Assert.True(drain.Idle);

        // 100/s for 4 s, 0.5% leech; half the chaos damage gets through (factor 0.5).
        drain.Apply(0, new(100, 4, 50));
        var (damage, leech) = drain.Advance(1, 0.5);
        Assert.Equal(50, damage);
        Assert.Equal(0.25, leech, 6);

        // A stronger, shorter hit takes over (300/s until t = 2); the first instance resumes after it.
        drain.Apply(1, new(300, 1, 50));
        Assert.Equal(300, drain.Advance(2, 1).Damage);
        Assert.Equal(100, drain.Advance(3, 1).Damage);

        // A weaker hit with a later expiry does not lower the damage, then carries on alone (100/s ends at t = 4).
        drain.Apply(3, new(40, 3, 50));
        Assert.Equal(100 + 40 * 2, drain.Advance(6, 1).Damage);
        Assert.True(drain.Idle);
        Assert.Null(drain.Active);
        Assert.Equal(0, drain.Advance(7, 1).Damage);

        // A hit after the old instance expired (no advance between) counts only from when it landed: 100/s for 1 s, then
        // nothing for the 1 s gap, then 10/s for 1 s.
        var late = new MonsterEssenceDrain();
        late.Apply(0, new(100, 1, 0));
        late.Apply(2, new(10, 5, 0));
        Assert.Equal(100 + 10, late.Advance(3, 1).Damage);

        // Contagion spreads the strongest debuff once, for its remaining time, even after the death tick cleared it.
        drain.Apply(10, new(100, 4, 50));
        drain.Apply(10, new(250, 4, 50));
        drain.Clear();
        var spread = drain.TakeSpread(10.5);
        Assert.Equal(250, spread?.DamagePerSecond);
        Assert.Equal(3.5, spread!.Seconds, 6);
        Assert.Null(drain.TakeSpread(10.5));
    }

    [Fact]
    public void Essence_drain_is_a_catalog_projectile()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("EssenceDrain");
        Assert.True(effect >= 0);
        Assert.True(skills.IsProjectile(effect));
        Assert.False(skills.IsAttack(effect));
        // Essence Drain keeps its old name's projectile row (Siphon), at its speed of 750.
        Assert.Equal(("Metadata/Projectiles/Siphon", 750), skills.ProjectileFor(effect));
    }
}
