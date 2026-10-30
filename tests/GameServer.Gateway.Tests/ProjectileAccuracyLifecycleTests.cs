using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectileAccuracyLifecycleTests
{
    sealed class Draws(params int[] values) : Random
    {
        public int Calls;
        public override int Next(int maxValue)
        {
            Assert.Equal(100, maxValue);
            Assert.True(Calls < values.Length, "Unexpected accuracy draw");
            return values[Calls++];
        }
    }

    static ProjectileRuntimeFixture Fixture() => new(new(new(ushort.MaxValue, "accuracy flight", 400, 400, 10, 100, null, []), 1, 1)
        { PlayerLife = 100 });
    static SpawnedMonster Target(ProjectileRuntimeFixture f, uint id, int x, bool block = false)
    {
        var target = new SpawnedMonster("accuracy target", new(x, 100, 10, width: 400, height: 400), 0, 0, 100)
        { Mods = block ? new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>
            { ["monster_base_block_%"] = 100 })) : new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int>())) };
        f.Conn.Monsters.Add(id, target);
        return target;
    }
    static void Release(ProjectileRuntimeFixture f, int pierce, bool returning, double speed = 200)
    {
        var profile = new ProjectileProfile("Metadata/Projectiles/DefaultArrow")
            { Speed = speed, Range = 100, Radius = 1, Pierce = pierce - 1, Returns = returning };
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 10, 10), false, 0, 0, 150, "accuracy")
            { AccuracyProfile = new(0) };
        f.Release(f.Shot(profile, offense, new(100, 100), new(300, 100)) with
            { ReturnAnchor = new(Program.PlayerId, f.Conn.ProjectileEpoch) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaded_projectile_contacts_spend_no_pierce_budget_and_trigger_no_hit(bool returning)
    {
        await using var f = Fixture();
        var miss = Target(f, 42, returning ? 90 : 130);
        var second = Target(f, 43, returning ? 60 : 160);
        var third = Target(f, 44, returning ? 30 : 190);
        miss.Impale.Apply(100, now: 0);
        var random = new Draws(0, 99, 99); f.Conn.AttackAccuracyRandom = random;
        await f.HoldOutput(); Release(f, 2, returning); f.Advance(2);
        Assert.Equal(100u, miss.Life); Assert.Equal(90u, second.Life); Assert.Equal(90u, third.Life);
        Assert.Equal(5, miss.AttackEvasion.Value);
        Assert.Equal(4, second.AttackEvasion.Value); Assert.Equal(4, third.AttackEvasion.Value);
        Assert.Equal(3, random.Calls); Assert.Equal(10, miss.Impale.Bonus);
        Assert.Equal(0, f.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_projectile_contacts_spend_pierce_budget_after_evasion(bool returning)
    {
        await using var f = Fixture();
        var blocked = Target(f, 42, returning ? 90 : 130, block: true);
        var second = Target(f, 43, returning ? 60 : 160);
        var untouched = Target(f, 44, returning ? 30 : 190);
        var random = new Draws(99, 99); f.Conn.AttackAccuracyRandom = random;
        Release(f, 2, returning); f.Advance(2);
        Assert.Equal(100u, blocked.Life); Assert.Equal(90u, second.Life); Assert.Equal(100u, untouched.Life);
        Assert.Equal(-1, untouched.AttackEvasion.Value); Assert.Equal(2, random.Calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Evaded_contact_does_not_reserve_the_target_for_later_projectiles(int pierce, bool returning)
    {
        await using var f = Fixture();
        var target = Target(f, 42, returning ? 90 : 130);
        var random = new Draws(90); f.Conn.AttackAccuracyRandom = random;
        Release(f, pierce, returning); f.Advance(2);
        Assert.Equal(100u, target.Life);
        Release(f, pierce, returning); f.Advance(4);
        Assert.Equal(90u, target.Life); Assert.Equal(1, random.Calls);
    }

    [Theory]
    [InlineData(1, "pause")]
    [InlineData(2, "pause")]
    [InlineData(1, "transfer")]
    [InlineData(2, "transfer")]
    [InlineData(1, "target-death")]
    [InlineData(2, "target-death")]
    public async Task Projectile_contact_checks_current_admission_before_accuracy(int pierce, string change)
    {
        await using var f = Fixture();
        var target = Target(f, 42, 130);
        var random = new Draws(99); f.Conn.AttackAccuracyRandom = random;
        await f.HoldOutput(); Release(f, pierce, false, speed: 50); f.Advance(.1);
        Assert.Equal(0, random.Calls); Assert.Equal(100u, target.Life);
        if (change == "pause") f.Conn.Clock.Stop();
        if (change == "transfer") Assert.True(f.Conn.TryBeginAreaChange(new(2, 0, false), out _));
        if (change == "target-death") { target.Dead = true; target.Life = 0; }
        if (change == "pause")
        {
            f.Session.Advance(); Assert.Equal(0, random.Calls); Assert.Equal(100u, target.Life);
            f.Conn.Clock.Start();
        }
        f.Advance(3);
        Assert.Equal(change == "pause" ? 1 : 0, random.Calls);
        Assert.Equal(change == "pause" ? 90u : change == "target-death" ? 0u : 100u, target.Life);
        Assert.Equal(0, f.Available);
    }
}
