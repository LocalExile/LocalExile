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
        int poolMovement = f.Player.Pools.MovementVelocityPct;
        int publishedMovement = f.PublishedMovement;
        double walkSpeed = f.Player.Walk.Speed;
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
        Assert.Equal(poolMovement + 45, f.Player.Pools.MovementVelocityPct);
        Assert.Equal(publishedMovement + 45, f.PublishedMovement);
        Assert.True(f.Player.Walk.Speed > walkSpeed);

        f.Player.Area.Buffs.Publish();
        var swiftness = InstanceEntities.AddBuff(f.Player.Id, instance, 2221, f.Player.Id, u68: Buff.Seconds(1.5));
        Assert.Contains(f.Player.Area.Delta.Take(), record => record.Bytes.AsSpan().SequenceEqual(swiftness));

        f.Advance(1.51);
        Game.CheckMomentumPresentation(f.Player, f.At);
        Assert.Equal(movement, f.Movement);
        Assert.Equal(poolMovement, f.Player.Pools.MovementVelocityPct);
        Assert.Equal(publishedMovement, f.PublishedMovement);
        Assert.Equal(walkSpeed, f.Player.Walk.Speed, 6);

        // Swiftness duration scales with Skill Effect Duration and Buff Duration, and ClearMomentum restores pools/speed.
        using var durationStats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, f.Player))
            {
                values[Stat.SkillEffectDurationPlusPct] = values.GetValueOrDefault(Stat.SkillEffectDurationPlusPct) + 40;
                values[Stat.BuffDurationPlusPct] = values.GetValueOrDefault(Stat.BuffDurationPlusPct) + 20;
            }
            return values;
        });
        ushort scaledInstance = f.Player.Area.Buffs.Next;
        Assert.True(await f.Cast());
        Assert.True(await f.Cast());
        Assert.True(await f.Cast());
        Assert.Equal(poolMovement + 45, f.Player.Pools.MovementVelocityPct);
        Assert.Equal(publishedMovement + 45, f.PublishedMovement);
        f.Player.Area.Buffs.Publish();
        var scaledSwiftness = InstanceEntities.AddBuff(f.Player.Id, scaledInstance, 2221, f.Player.Id, u68: Buff.Seconds(2.4));
        Assert.Contains(f.Player.Area.Delta.Take(), record => record.Bytes.AsSpan().SequenceEqual(scaledSwiftness));

        f.Advance(1.8);
        Game.CheckMomentumPresentation(f.Player, f.At);
        Assert.Equal(poolMovement + 45, f.Player.Pools.MovementVelocityPct);
        Assert.Equal(publishedMovement + 45, f.PublishedMovement);
        Assert.True(f.Player.Walk.Speed > walkSpeed);

        Game.ClearMomentum(f.Player);
        Assert.Equal(poolMovement, f.Player.Pools.MovementVelocityPct);
        Assert.Equal(publishedMovement, f.PublishedMovement);
        Assert.Equal(walkSpeed, f.Player.Walk.Speed, 6);
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

        // A travelling Shield Charge linked to Momentum clears existing stacks and does not grant a stationary stack on arrival.
        var items = Game.ItemsOf(f.Player);
        items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
            _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
        ushort[] linked = f.EquipMany(("Heavy Strike", 1), ("Shield Charge", 1), ("Momentum Support", 1));
        ushort heavy = linked[0], charge = linked[1];
        int chargeBase = Game.SupportedSkill(f.Player, charge).Stats.GetValueOrDefault(Stat.AttackSpeedPlusPct);
        Game.ClearMomentum(f.Player);
        f.Player.NextAttackAt = 0;
        var chargeStart = f.Player.Walk.Position(f.At);
        Assert.True(await Game.CastGemSkill(f.Player, charge, chargeStart, (chargeStart.X + 20, chargeStart.Y)));
        Assert.NotEqual(chargeStart, f.Player.Walk.Position(f.At));
        Assert.Equal(chargeBase, Game.SupportedSkill(f.Player, charge).Stats.GetValueOrDefault(Stat.AttackSpeedPlusPct));

        f.Player.NextAttackAt = 0;
        var standing = f.Player.Walk.Position(f.At);
        Assert.True(await Game.CastGemSkill(f.Player, heavy, standing, (standing.X + 3, standing.Y)));
        Assert.Equal(chargeBase + 15, Game.SupportedSkill(f.Player, charge).Stats.GetValueOrDefault(Stat.AttackSpeedPlusPct));
        f.Player.NextAttackAt = 0;
        Assert.True(await Game.CastGemSkill(f.Player, charge, standing, (standing.X - 20, standing.Y)));
        Assert.NotEqual(standing, f.Player.Walk.Position(f.At));
        Assert.Equal(chargeBase, Game.SupportedSkill(f.Player, charge).Stats.GetValueOrDefault(Stat.AttackSpeedPlusPct));
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
        public int PublishedMovement => Game.PlayerStatValues(Player).GetValueOrDefault(Stat.MovementVelocityPlusPct);

        public Fixture()
        {
            Player.ProjectileRuntimeState = fixture.Session;
            Player.Walk.Teleport(100, 100, At);
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, this);
            Skill = Assert.Single(EquipMany(("Heavy Strike", 1), ("Momentum Support", 1)));
            Assert.Equal(15, Game.SupportedSkill(Player, Skill).Stats[Stat.SupportMomentumAttackSpeedPlusPctPerStack]);
            Game.RecomputePoolsAndSpeed(Player);
        }

        public ushort[] EquipMany(params (string Name, int Level)[] gems) => fixture.Equip(gems);

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
