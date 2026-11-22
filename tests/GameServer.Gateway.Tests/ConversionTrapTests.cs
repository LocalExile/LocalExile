using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ConversionTrapTests
{
    [Fact]
    public async Task Conversion_trap_changes_a_normal_enemys_target_until_the_skill_effect_expires()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort skill = Assert.Single(f.Equip(("Conversion Trap", 1)));
        var gem = Game.SupportedSkill(conn, skill);
        Assert.True(gem.RequirementsMet);
        uint mana = conn.PlayerMana;
        uint cost = Game.ManaCost(Program.Stats, Game.SkillsFor(conn), gem.Effect, gem.Level, 0);
        var clock = new AreaClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            var trap = await Throw(f, skill);
            Assert.Equal(mana - cost, conn.PlayerMana);
            Assert.Equal(1, Game.SingleUseState(conn, gem.Effect, skill)!.SpentUses);

            var converted = SpawnEnemy(conn, 704, trap.At);
            converted.Target = conn;
            var enemy = SpawnEnemy(conn, 705, (trap.At.X + 4, trap.At.Y));
            clock.At = trap.Armed + .01;
            await Tick(f, clock.At);
            Assert.Empty(conn.Placed.All);
            Assert.False(converted.Hostile);
            Assert.Equal(converted.MaxLife, converted.Life);
            Assert.Null(converted.Target);
            Assert.True(enemy.Hostile);

            var cadence = new MonsterAiCadence();
            var allies = Game.PlanMonsterAi(conn, cadence, clock.At);
            var swing = Assert.Single(allies.MinionSwings);
            Assert.Same(converted, swing.Monster);
            Assert.Equal(enemy.ObjectId, swing.TargetId);
            Assert.DoesNotContain(allies.Swings, order => ReferenceEquals(order.Monster, converted));
            lock (conn.Monsters)
            {
                var hit = Game.MinionHitTargetLocked(conn, converted.ObjectId, converted, enemy.ObjectId, enemy,
                    clock.At, new Random(42), checkReach: true, accuracy: 100_000);
                Assert.NotNull(hit);
            }
            Assert.True(enemy.Life < enemy.MaxLife);

            int duration = gem.Stats[Program.Stats.Key("base_skill_effect_duration")!.Value];
            double expiry = clock.At + duration / 1000d;
            Game.PlanMonsterAi(conn, cadence, expiry - .05);
            Assert.False(converted.Hostile);
            Game.PlanMonsterAi(conn, cadence, expiry + .01);
            Assert.True(converted.Hostile);
            Assert.Same(conn, converted.Target);
            Assert.False(conn.Minions.Attacks.ContainsKey(704));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Conversion_trap_leaves_a_unique_hostile_and_does_not_trigger_at_expiry()
    {
        using var f = new TriggeredSummonTests.Fixture();
        ushort skill = Assert.Single(f.Equip(("Conversion Trap", 1)));
        var clock = new AreaClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            var gem = Game.SupportedSkill(f.Conn, skill);
            Assert.True(Game.TryBeginSkillCooldownUse(f.Conn, skill, gem.Effect, Game.CooldownForSkill(f.Conn, skill),
                false, () => { }, out var use, actionToken: f.Conn.ActionCount));
            Assert.NotNull(use);
            uint manaAfterPreflight = f.Conn.PlayerMana;
            var trap = await Throw(f, skill, use);
            Assert.Equal(manaAfterPreflight, f.Conn.PlayerMana);
            Assert.Equal(1, Game.SingleUseState(f.Conn, gem.Effect, skill)!.SpentUses);
            var unique = SpawnEnemy(f.Conn, 704, trap.At, MonsterRarity.Unique);
            var nearby = SpawnEnemy(f.Conn, 705, (trap.At.X + 1, trap.At.Y));
            clock.At = trap.Armed + .01;
            await Tick(f, clock.At);
            Assert.Empty(f.Conn.Placed.All);
            Assert.True(unique.Hostile);
            Assert.Equal(unique.MaxLife, unique.Life);
            Assert.True(nearby.Hostile);

            f.Conn.Monsters.Clear();
            trap = await Throw(f, skill);
            clock.At = trap.Until - .05;
            await Tick(f, clock.At);
            Assert.Same(trap, Assert.Single(f.Conn.Placed.All.Values));
            var late = SpawnEnemy(f.Conn, 706, trap.At);
            clock.At = trap.Until;
            await Tick(f, clock.At);
            Assert.Empty(f.Conn.Placed.All);
            Assert.True(late.Hostile);
            Assert.Equal(late.MaxLife, late.Life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static async Task<PlacedSkills.Placed> Throw(TriggeredSummonTests.Fixture f, ushort skill,
        CooldownUseTicket? cooldownUse = null)
    {
        f.Conn.Wire = ("conversion-trap", f.Stream, f.Ciphers);
        var from = f.Conn.Walk.Position(0);
        Assert.True(await Game.CastGemSkill("conversion-trap", f.Stream, f.Ciphers, f.Conn,
            skill, from, (from.X + 12, from.Y), actionToken: cooldownUse is null ? null : f.Conn.ActionCount,
            cooldownUse: cooldownUse));
        return Assert.Single(f.Conn.Placed.All.Values);
    }

    static SpawnedMonster SpawnEnemy(ClientState conn, uint id, (int X, int Y) at, MonsterRarity rarity = MonsterRarity.Normal)
    {
        var monster = new SpawnedMonster("enemy", new(at.X, at.Y, 0), 60, 4, 1000)
        {
            ObjectId = id, Rarity = rarity,
            Attack = new(0x2909, .5, 10, DamagePacket.Of(DamageType.Physical, new(10, 10)))
        };
        conn.Monsters.Add(id, monster);
        conn.MonsterViews.Add(id, new(at, at, monster.Life));
        return monster;
    }

    static Task Tick(TriggeredSummonTests.Fixture f, double now) =>
        (Task)typeof(Game).GetMethod("PlacedSkillsTick", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["conversion-trap", f.Stream, f.Ciphers, f.Conn, now])!;

    sealed class AreaClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
