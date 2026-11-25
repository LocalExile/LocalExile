using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static MonsterElementalAilments;

namespace GameServer.Gateway.Tests;

public sealed class GlacialHammerTests
{
    [Fact]
    public async Task Glacial_hammer_amplifies_only_the_third_strikes_freeze_and_shatters_frozen_low_life_enemies()
    {
        using var swing = new HammerFixture("Glacial Hammer", neverCrit: true);
        var first = swing.Target(901, 107);
        var second = swing.Target(902, 93);
        await swing.Hit(first);
        Assert.Equal(0, first.Ailments.Remaining(Kind.Freeze, swing.Now));
        await swing.Hit(second);
        Assert.Equal(0, second.Ailments.Remaining(Kind.Freeze, swing.Now));
        uint before = first.Life;
        await swing.Hit(first);
        Assert.True(first.Ailments.Remaining(Kind.Freeze, swing.Now) > 0.3);
        Assert.InRange(before - first.Life, 100u, 150u);

        first.Life = 2100;
        swing.Freeze(first);
        await swing.Hit(first);
        Assert.Equal(0u, first.Life);
        Assert.True(first.Ailments.Shattered);

        second.Life = 2100;
        await swing.Hit(second);
        Assert.True(second.Life > 0);
        Assert.False(second.Ailments.Shattered);

        var unique = swing.Target(903, 106, MonsterRarity.Unique);
        unique.Life = 2100;
        swing.Freeze(unique);
        await swing.Hit(unique);
        Assert.True(unique.Life > 0);
        Assert.False(unique.Ailments.Shattered);
    }

    [Fact]
    public async Task Shattering_guarantees_crit_on_each_third_strike_and_movement_restarts_the_sequence()
    {
        await CheckCriticalSequence();
        using var repeated = new HammerFixture("Glacial Hammer of Shattering", multistrike: true);
        var repeatTarget = repeated.Target(901, 107);
        await repeated.Hit(repeatTarget);
        Assert.Equal(1, repeated.Player.RecentEvents.RecentAttackCriticals(repeated.Now));
    }

    static async Task CheckCriticalSequence()
    {
        using var swing = new HammerFixture("Glacial Hammer of Shattering");
        var target = swing.Target(901, 107);
        for (int strike = 1; strike <= 6; strike++)
        {
            uint before = target.Life;
            await swing.Hit(target);
            Assert.Equal(strike % 3 == 0, before - target.Life > 150);
        }

        await swing.Hit(target);
        await swing.Hit(target);
        swing.Player.Walk.Teleport(101, 100, swing.Clock.At);
        uint life = target.Life;
        await swing.Hit(target);
        Assert.InRange(life - target.Life, 100u, 150u);

        swing.PreventCriticals = true;
        for (int strike = 0; strike < 2; strike++)
        {
            life = target.Life;
            await swing.Hit(target);
            Assert.InRange(life - target.Life, 100u, 150u);
        }
    }

    sealed class HammerFixture : IDisposable
    {
        internal readonly Clock Clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly IDisposable stats;
        readonly PlayerUpdatesState? previous;
        readonly ushort skill;
        internal Player Player => fixture.Player;
        internal double Now => Game.ProjectileWorldTime(Player);
        internal bool PreventCriticals;

        internal HammerFixture(string name, bool neverCrit = false, bool multistrike = false)
        {
            fixture = new(Clock);
            Player.Use(new(1, "hammer-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(100, 100, Clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, Player)) return values;
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.AttackMinimumAddedPhysicalDamage] = 50;
                values[Stat.AttackMaximumAddedPhysicalDamage] = 50;
                values[Stat.ChanceToFreezePct] = 100;
                values[Stat.CriticalStrikeChancePlusPct] = -100;
                if (neverCrit || PreventCriticals) values[Stat.GlobalCannotCrit] = 1;
                return values;
            });
            skill = Assert.Single(multistrike
                ? fixture.Equip((name, 1), ("Multistrike Support", 1))
                : fixture.Equip((name, 1)));
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, Clock);
        }

        internal SpawnedMonster Target(uint id, int x, MonsterRarity rarity = MonsterRarity.Normal)
        {
            var target = new SpawnedMonster("glacial hammer witness", new(x, 100, 0), 0, 0, 6500)
                { ObjectId = id, Hostile = true, NoRewards = true, Rarity = rarity };
            Player.Area.Monsters[id] = target;
            Player.Area.Grid.Set(new(id), (x, 100));
            return target;
        }

        internal void Freeze(SpawnedMonster target) =>
            Assert.Contains(target.Ailments.Inflict(DamagePacket.Empty.Add(DamageType.Cold, new(6500, 6500)),
                true, target.MaxLife, Now, _ => 0, _ => 0, Random.Shared), effect => effect.Kind == Kind.Freeze);

        internal async Task Hit(SpawnedMonster target)
        {
            Clock.At = Math.Max(Clock.At + .05, Player.NextAttackAt + .01);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
            int action = Interlocked.Increment(ref Player.ActionCount);
            Assert.True(await Game.CastGemSkill(Player, skill, Player.Walk.Position(Clock.At),
                target.Walk.Position(Clock.At), target.ObjectId, sequence: Player.NextActionSequence(), actionToken: action));
        }

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            stats.Dispose();
            fixture.Dispose();
        }
    }

    internal sealed class Clock : IPlayerUpdateSchedule
    {
        internal double At = 3;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
