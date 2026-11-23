using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class FirestormTests
{
    [Fact]
    public async Task Cwdt_pays_once_and_firestorm_has_a_larger_first_hit_followed_by_timed_impacts()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Player;
        conn.Clock.Restart();
        var clock = new HitClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            conn.Walk.Teleport(100, 100, 0);
            ushort skill = Assert.Single(fixture.Equip(("Firestorm", 1), ("Cast when Damage Taken Support", 1)));
            var gem = Game.SupportedSkill(conn, skill);
            Assert.NotNull(Game.GemSkillInstanceDisabled(conn, gem.Effect, skill));
            string? effectId = Game.SkillsFor(conn).EffectId(gem.Effect);
            Assert.True(Game.TriggerPayloadAvailable(conn, gem),
                $"Executor unavailable for {effectId}, plan {SkillHitPlan.ForSkill(Game.SkillsFor(conn), gem.Effect, gem.Level).CanHit}.");
            Assert.Null(Game.GemSkillInstanceDisabled(conn, gem.Effect, skill, triggered: true));
            uint mana = conn.PlayerMana;
            int threshold = gem.Stats[Program.Stats.Key("cast_on_damage_taken_threshold")!.Value];
            var targets = new List<SpawnedMonster>();
            for (int x = 80; x <= 120; x += 10)
            for (int y = 80; y <= 120; y += 10)
            {
                var target = new SpawnedMonster("storm target", new(x, y, 0), 0, 0, 10000);
                conn.Area.Monsters[(uint)(200 + targets.Count)] = target;
                conn.Area.Zones.Grid.Set(new((uint)(200 + targets.Count)), (x, y));
                targets.Add(target);
            }
            long TotalLife() => targets.Sum(target => (long)target.Life);
            long intact = TotalLife();
            async Task Advance(double seconds)
            {
                clock.At = seconds;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(seconds));
                var fence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Session.Enqueue(() => { fence.SetResult(); return Task.CompletedTask; });
                await fence.Task;
            }

            Game.RecordDamageTakenTriggers(conn, threshold - 1, 0);
            Game.ReleaseDamageTakenTriggers(conn, fixture.Session);
            Assert.Equal(mana, conn.PlayerMana);
            Game.RecordDamageTakenTriggers(conn, 1, 0);
            Game.ReleaseDamageTakenTriggers(conn, fixture.Session);
            uint afterPayment = conn.PlayerMana;
            Assert.True(afterPayment < mana, $"No skill payment for {effectId}. Mana {mana}, after {afterPayment}, in town {Game.InTown(conn)}, rules {Game.CurrentGameRules}, threshold {threshold}, rate {gem.Stats.GetValueOrDefault(Stat.CastWhenDamageTakenTriggerThresholdPlusPct)}.");
            await Advance(.149);
            Assert.Equal(intact, TotalLife());
            await Advance(.15);
            long afterFirst = TotalLife();
            Assert.True(afterFirst < intact);
            await Advance(.30);
            Assert.True(TotalLife() < afterFirst);
            await Advance(1.4);
            long afterStorm = TotalLife();
            await Advance(2);
            Assert.Equal(afterStorm, TotalLife());
            Assert.Equal(afterPayment, conn.PlayerMana);

            ushort manual = Assert.Single(fixture.Equip(("Firestorm", 1)));
            Assert.Null(Game.GemSkillInstanceDisabled(conn, Game.SupportedSkill(conn, manual).Effect, manual));
            conn.ProjectileRuntimeState = fixture.Session;
            Assert.True(await Game.CastGemSkill(conn, manual, (100, 100), (100, 100)));
            await Advance(3.5);
            Assert.True(TotalLife() < afterStorm);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class HitClock : IPlayerUpdateSchedule
    {
        public double At;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}