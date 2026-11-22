using System.Reflection;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class FireTrapDamageTests
{
    [Fact]
    public async Task Trap_damage_scales_the_fire_trap_burst_and_its_burning_ground()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        conn.Wire = ("fire-trap-test", f.Stream, f.Ciphers);
        ushort skill = Assert.Single(f.Equip(("Fire Trap", 1)));
        var clock = new TestClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            uint generic = Program.Stats.Key("damage_+%")!.Value;
            uint trapStat = Program.Stats.Key("trap_damage_+%")!.Value;
            var baseline = await Trigger();
            conn.PlayerStats[generic] = 12;
            var control = await Trigger();
            conn.PlayerStats.Remove(generic);
            conn.PlayerStats[trapStat] = 12;
            var actual = await Trigger();

            Assert.True(control.Hit > baseline.Hit);
            Assert.True(control.Ground > baseline.Ground);
            Assert.Equal(control.Hit, actual.Hit, 6);
            Assert.Equal(control.Ground, actual.Ground, 6);

            ushort selfCast = Assert.Single(f.Equip(("Fireball", 1)));
            double Fireball() => Game.SupportedHitFor(conn, selfCast, suppress: false)[DamageType.Fire].Max;
            double withTrapStat = Fireball();
            conn.PlayerStats.Remove(trapStat);
            Assert.Equal(Fireball(), withTrapStat, 6);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        async Task<(double Hit, double Ground)> Trigger()
        {
            var from = conn.Walk.Position(clock.At);
            Assert.True(await Game.CastGemSkill("fire-trap-test", f.Stream, f.Ciphers, conn, skill,
                from, (from.X + 12, from.Y)));
            var trap = Assert.Single(conn.Placed.All.Values);
            double hit = trap.Damage[DamageType.Fire].Max;
            var enemy = new SpawnedMonster("target", new(trap.At.X, trap.At.Y, 0), 50, 4, 1000);
            conn.Monsters.Add(700, enemy);
            clock.At = trap.Armed + .01;
            var tick = typeof(Game).GetMethod("PlacedSkillsTick", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task)tick.Invoke(null, ["fire-trap-test", f.Stream, f.Ciphers, conn, clock.At])!;
            Assert.True(enemy.Life < enemy.MaxLife);
            Assert.Empty(conn.Placed.All);
            double dps = Assert.Single(conn.Placed.Grounds).DamagePerSecond;
            conn.Monsters.Remove(700);
            conn.Placed.Grounds.Clear();
            return (hit, dps);
        }
    }

    sealed class TestClock : IPlayerUpdateSchedule
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
