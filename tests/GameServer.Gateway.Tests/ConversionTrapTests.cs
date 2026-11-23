using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ConversionTrapTests
{
    [Fact]
    public async Task Conversion_trap_changes_a_normal_enemys_allegiance_and_target_until_the_effect_expires()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Conversion Trap", 1)));
        var supported = Game.SupportedSkill(player, skill);
        Assert.True(supported.RequirementsMet);
        uint mana = player.PlayerMana;
        uint cost = Game.ManaCost(Program.Stats, Game.SkillsFor(player), supported.Effect, supported.Level, 0);
        var clock = new AreaClock();
        var prior = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var placed = await Throw(player, skill, preflight: true);
            Assert.Equal(mana - cost, player.PlayerMana);
            Assert.Equal(1, Game.SingleUseState(player, supported.Effect, skill)!.SpentUses);

            var converted = SpawnEnemy(player, 704, placed.At);
            converted.Target = player;
            var enemy = SpawnEnemy(player, 705, (placed.At.X + 4, placed.At.Y));
            clock.At = placed.Armed + .01;
            await Tick(player, clock.At);
            Assert.Empty(player.Placed.All);
            Assert.False(converted.Hostile);
            Assert.Equal(converted.MaxLife, converted.Life);
            Assert.Null(converted.Target);
            Assert.True(enemy.Hostile);
            Assert.True(player.Area.Buffs.HasRow(converted.ObjectId, 165));

            var cadence = new MonsterAiCadence();
            var plan = Game.PlanMonsterAi(player, cadence, clock.At);
            var swing = Assert.Single(plan.MinionSwings);
            Assert.Same(converted, swing.Monster);
            Assert.Equal(enemy.ObjectId, swing.TargetId);
            Assert.DoesNotContain(plan.Swings, order => ReferenceEquals(order.Monster, converted));
            Assert.NotNull(Game.MinionHitTargetLocked(player, converted.ObjectId, converted, enemy.ObjectId, enemy,
                clock.At, new Random(42), checkReach: true, accuracy: 100_000));
            Assert.True(enemy.Life < enemy.MaxLife);

            int duration = supported.Stats[Stat.BaseSkillEffectDuration];
            double expiry = clock.At + duration / 1000d;
            clock.At = expiry - .05;
            Game.AreaAilmentChangesLocked(player, clock.At);
            Assert.False(converted.Hostile);
            clock.At = expiry + .01;
            Game.AreaAilmentChangesLocked(player, clock.At);
            Game.PlanMonsterAi(player, cadence, clock.At);
            Assert.True(converted.Hostile);
            Assert.Same(player, converted.Target);
            Assert.False(player.Area.Buffs.HasRow(converted.ObjectId, 165));
            Assert.False(player.Minions.Attacks.ContainsKey(converted.ObjectId));
        }
        finally { Game.AreaUpdateContext.Value = prior; }
    }

    [Fact]
    public async Task Conversion_trap_leaves_a_unique_hostile_and_does_not_trigger_at_expiry()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Conversion Trap", 1)));
        var clock = new AreaClock();
        var prior = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var placed = await Throw(player, skill, preflight: true);
            var unique = SpawnEnemy(player, 704, placed.At, MonsterRarity.Unique);
            var nearby = SpawnEnemy(player, 705, (placed.At.X + 1, placed.At.Y));
            clock.At = placed.Armed + .01;
            await Tick(player, clock.At);
            Assert.Empty(player.Placed.All);
            Assert.True(unique.Hostile);
            Assert.Equal(unique.MaxLife, unique.Life);
            Assert.True(nearby.Hostile);
            Assert.False(player.Area.Buffs.HasRow(unique.ObjectId, 165));

            player.Area.Monsters.Clear();
            clock.At += 10;
            placed = await Throw(player, skill, preflight: false);
            clock.At = placed.Until - .05;
            await Tick(player, clock.At);
            Assert.Same(placed, Assert.Single(player.Placed.All.Values));
            var late = SpawnEnemy(player, 706, placed.At);
            clock.At = placed.Until;
            await Tick(player, clock.At);
            Assert.Empty(player.Placed.All);
            Assert.True(late.Hostile);
            Assert.Equal(late.MaxLife, late.Life);
        }
        finally { Game.AreaUpdateContext.Value = prior; }
    }

    static async Task<PlacedSkills.Placed> Throw(Player player, ushort skill, bool preflight)
    {
        var supported = Game.SupportedSkill(player, skill);
        CooldownUseTicket? ticket = null;
        if (preflight)
        {
            Assert.True(Game.TryBeginSkillCooldownUse(player, skill, supported.Effect, Game.CooldownForSkill(player, skill),
                instant: false, () => { }, out ticket, actionToken: player.ActionCount));
            Assert.NotNull(ticket);
        }
        var from = player.Walk.Position(Game.AreaTime(player));
        Assert.True(await Game.CastGemSkill(player, skill, from, (from.X + 12, from.Y),
            actionToken: preflight ? player.ActionCount : null, cooldownUse: ticket));
        return Assert.Single(player.Placed.All.Values);
    }

    static SpawnedMonster SpawnEnemy(Player player, uint id, (int X, int Y) at, MonsterRarity rarity = MonsterRarity.Normal)
    {
        var monster = new SpawnedMonster("conversion target", new(at.X, at.Y, 0), 60, 4, 1000)
        {
            ObjectId = id,
            Rarity = rarity,
            Attack = new(0x2909, .5, 10, DamagePacket.Of(DamageType.Physical, new(10, 10))),
        };
        player.Area.Monsters.Add(id, monster);
        return monster;
    }

    static Task Tick(Player player, double now) =>
        (Task)typeof(Game).GetMethod("PlacedSkillsTick", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [player, now])!;

    sealed class AreaClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
