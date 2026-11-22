using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class TriggeredFirestormTests
{
    [Fact]
    public async Task Cwdt_pays_once_and_firestorm_has_a_larger_first_hit_followed_by_timed_impacts()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        conn.Clock.Reset();
        conn.Clock.Start();
        conn.Walk.Teleport(100, 100, 0);
        ushort skill = Assert.Single(fixture.Equip(("Firestorm", 1), ("Cast when Damage Taken Support", 1)));
        var gem = Game.SupportedSkill(conn, skill);
        Assert.NotNull(Game.GemSkillInstanceDisabled(conn, gem.Effect, skill));
        uint mana = conn.PlayerMana;
        int threshold = gem.Stats[Program.Stats.Key("cast_on_damage_taken_threshold")!.Value];
        var targets = new List<SpawnedMonster>();
        for (int x = 76; x <= 124; x += 8)
        for (int y = 76; y <= 124; y += 8)
        {
            var target = new SpawnedMonster("storm target", new(x, y, 0), 0, 0, 10000);
            conn.Monsters[(uint)(200 + targets.Count)] = target;
            targets.Add(target);
        }
        long TotalLife() => targets.Sum(target => (long)target.Life);
        long intact = TotalLife();
        async Task Advance(double seconds)
        {
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
        Assert.True(afterPayment < mana);
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
        Assert.True(await Game.CastGemSkill("firestorm", fixture.Stream, fixture.Ciphers, conn, manual, (100, 100), (100, 100)));
        await Advance(Math.Max(3.5, conn.Clock.Elapsed.TotalSeconds + .9));
        Assert.True(TotalLife() < afterStorm);
    }
}
