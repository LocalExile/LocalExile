using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MomentumSupportTests
{
    [Fact]
    public async Task Paid_stationary_attacks_speed_up_the_next_attack_and_spend_the_cap_on_swiftness()
    {
        using var f = new Fixture();
        int baseline = f.SkillSpeed;
        int movement = f.Movement;
        double initial = Game.SupportedAttackSeconds(f.Player, f.Skill);

        f.Player.PlayerMana = 0;
        Assert.False(await f.Cast());
        Assert.Equal(baseline, f.SkillSpeed);
        f.Player.PlayerMana = 10000;

        Assert.True(await f.Cast());
        Assert.Equal(baseline + 15, f.SkillSpeed);
        Assert.True(Game.SupportedAttackSeconds(f.Player, f.Skill) < initial);
        Assert.True(await f.Cast());
        Assert.Equal(baseline + 30, f.SkillSpeed);
        ushort instance = f.Player.Area.Buffs.Next;
        Assert.True(await f.Cast());
        Assert.Equal(baseline, f.SkillSpeed);
        Assert.Equal(movement + 45, f.Movement);

        f.Player.Area.Buffs.Publish();
        var swiftness = InstanceEntities.AddBuff(f.Player.Id, instance, 2221, f.Player.Id, u68: Buff.Seconds(1.5));
        Assert.Contains(f.Player.Area.Delta.Take(), record => record.Bytes.AsSpan().SequenceEqual(swiftness));

        f.Advance(1.51);
        Assert.Equal(movement, f.Movement);
    }

    [Fact]
    public async Task Actual_travel_and_teleport_erase_stacks_before_the_next_attack()
    {
        using var f = new Fixture();
        int baseline = f.SkillSpeed;
        Assert.True(await f.Cast());
        Assert.Equal(baseline + 15, f.SkillSpeed);

        var before = f.Player.Walk.Position(f.At);
        f.Player.Walk.SetSpeed(40, f.At);
        f.Player.Walk.Order(before.X + 20, before.Y, f.At);
        f.Advance(.6);
        Assert.NotEqual(before, f.Player.Walk.Position(f.At));
        Assert.Equal(baseline, f.SkillSpeed);

        Assert.True(await f.Cast());
        Assert.Equal(baseline + 15, f.SkillSpeed);
        f.Player.Walk.Teleport(108, 103, f.At);
        Assert.Equal(baseline, f.SkillSpeed);
    }

    sealed class Fixture : IDisposable, IPlayerUpdateSchedule
    {
        readonly TriggeredSummonTests.Fixture fixture = new();
        readonly PlayerUpdatesState? previous = Game.AreaUpdateContext.Value;
        public Player Player => fixture.Player;
        public ushort Skill { get; }
        public double At { get; private set; } = 1;
        public int SkillSpeed => Game.SupportedSkill(Player, Skill).Stats.GetValueOrDefault(Stat.AttackSpeedPlusPct);
        public int Movement => Game.PlayerStatValues(Player).GetValueOrDefault(Stat.BaseMovementVelocityPlusPct);

        public Fixture()
        {
            Player.ProjectileRuntimeState = fixture.Session;
            Player.Walk.Teleport(100, 100, At);
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, this);
            Skill = Assert.Single(fixture.Equip(("Heavy Strike", 1), ("Momentum Support", 1)));
            Assert.Equal(15, Game.SupportedSkill(Player, Skill).Stats[Stat.SupportMomentumAttackSpeedPlusPctPerStack]);
        }

        public Task<bool> Cast()
        {
            Player.NextAttackAt = 0;
            var from = Player.Walk.Position(At);
            return Game.CastGemSkill(Player, Skill, from, (from.X + 3, from.Y));
        }

        public void Advance(double seconds)
        {
            At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(At));
        }

        double IPlayerUpdateSchedule.Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            Advance(milliseconds / 1000d);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            fixture.Dispose();
        }
    }
}
