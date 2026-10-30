using System.Buffers.Binary;
using System.Diagnostics;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static async Task RunHarbingerClockUntil(ClientState conn, double at)
    {
        var bound = Stopwatch.StartNew();
        conn.Clock.Start();
        while (conn.Clock.Elapsed.TotalSeconds < at && bound.Elapsed.TotalSeconds < 5) await Task.Delay(1);
        conn.Clock.Stop(); // Publication sees an exact, stable game-clock sample after the held interval.
        Assert.True(conn.Clock.Elapsed.TotalSeconds >= at);
    }

    [Theory]
    [InlineData(.5, false)] [InlineData(.5, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public async Task Harbinger_recovery_projection_at_held_gate_expires_or_shortens_without_consuming_payment(
        double rate, bool expired)
    {
        using var game = HarbingerGame();
        var conn = game.State;
        conn.Clock.Stop();
        conn.PlayerStats[Program.Stats.Key("buff_time_passed_+%")!.Value] = (int)((rate - 1) * 100);
        double start = conn.Clock.Elapsed.TotalSeconds;
        const double budget = .2;
        var source = new Flasks.Effect(0, 100, 114, 0, 20, start, start + budget, ManaAtEnd: 17);
        conn.FlaskEffects.Start(source);
        await using var wire = new ProjectileRuntimeFixture(conn);
        await wire.HoldOutput();
        var publish = ProgramValue<Task<bool>>("SendRecoveryFlaskStart", "held-recovery", wire.Stream,
            wire.Ciphers, conn, source, true);
        Assert.False(publish.IsCompleted);
        await RunHarbingerClockUntil(conn, start + (expired ? budget / rate + .05 : budget / rate * .4));
        double gateTime = conn.Clock.Elapsed.TotalSeconds;
        // No Effects.Advance has run at all, including while the start waited for the send gate.
        Assert.Equal(budget, conn.FlaskEffects.Remaining(source), 8);
        wire.ReleaseOutput();
        Assert.Equal(!expired, await publish.WaitAsync(TimeSpan.FromSeconds(5)));
        var bytes = await wire.ReadWire();
        if (expired) Assert.Empty(bytes);
        else
        {
            var (stats, prefix) = HarbingerStatPrefix(bytes);
            Assert.Equal((int)((rate - 1) * 100), stats[Program.Stats.Key("buff_time_passed_+%")!.Value]);
            // The optional duration precedes this row's recovery GrantedStats word.
            float remaining = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(prefix + 23, 4)));
            Assert.Equal(budget - (gateTime - start) * rate, remaining, 6);
        }
        Assert.True(conn.FlaskEffects.Any);
        Assert.Equal(budget, conn.FlaskEffects.Remaining(source), 8);
        var actual = conn.FlaskEffects.Advance(start, start + 10, manaRecoveryRate: 1.5, expiryRate: rate);
        Assert.Equal(20 * budget / rate * 1.5 + 17, actual.Mana, 8);
        Assert.Equal(0, actual.Life);
        Assert.Same(source, Assert.Single(actual.Ended));
        var again = conn.FlaskEffects.Advance(start + 10, start + 20, manaRecoveryRate: 1.5, expiryRate: rate);
        Assert.Equal(0, again.Mana);
        Assert.Empty(again.Ended);
    }

    [Fact]
    public async Task Harbinger_recovery_projection_at_held_gate_keeps_waiting_budget_across_stronger_future_start()
    {
        using var game = HarbingerGame();
        var conn = game.State;
        conn.Clock.Stop();
        double start = conn.Clock.Elapsed.TotalSeconds;
        var weaker = new Flasks.Effect(0, 100, 114, 0, 20, start, start + .5, ManaAtEnd: 17);
        var stronger = new Flasks.Effect(1, 101, 114, 0, 100, start + .1, start + 10.1);
        conn.FlaskEffects.Start(weaker);
        conn.FlaskEffects.Start(stronger);
        await using var wire = new ProjectileRuntimeFixture(conn);
        await wire.HoldOutput();
        var publish = ProgramValue<Task<bool>>("SendRecoveryFlaskStart", "waiting-recovery", wire.Stream,
            wire.Ciphers, conn, weaker, true);
        Assert.False(publish.IsCompleted);
        await RunHarbingerClockUntil(conn, start + .7); // Past weaker.Ends, but the stronger source owns this interval.
        wire.ReleaseOutput();
        Assert.True(await publish.WaitAsync(TimeSpan.FromSeconds(5)));
        var bytes = await wire.ReadWire();
        int prefix = HarbingerStatPrefix(bytes).End;
        float remaining = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(prefix + 23, 4)));
        Assert.Equal(.4, remaining, 6);
        Assert.Equal(.5, conn.FlaskEffects.Remaining(weaker), 8);
        Assert.Equal(10, conn.FlaskEffects.Remaining(stronger), 8);
        var actual = conn.FlaskEffects.Advance(start, start + 20);
        Assert.Equal(1027, actual.Mana, 8); // 10 weaker +1000 stronger +17 at end, never paid by projection.
        Assert.Equal(2, actual.Ended.Count);
        Assert.Equal(0, conn.FlaskEffects.Advance(start + 20, start + 21).Mana);
    }

    [Fact]
    public void Harbinger_recovery_projection_uses_real_boundary_and_independent_queues_without_paying_endpoints()
    {
        var effects = new Flasks.Effects();
        var weak = new Flasks.Effect(0, 1, 113, 10, 0, 0, 4);
        var strong = new Flasks.Effect(1, 2, 113, 30, 0, 1, 2);
        var mana = new Flasks.Effect(2, 3, 114, 0, 20, .5, 2, ManaAtEnd: 17);
        var independent = new Flasks.Effect(3, 0, 0, 5, 0, 1, 1.5);
        foreach (var source in new[] { weak, strong, mana, independent }) effects.Start(source);
        Assert.Equal(5, effects.Advance(0, .5).Life);
        Assert.Equal(3.25, effects.ProjectedRemaining(weak, 2.5, .5), 8);
        Assert.Equal(.25, effects.ProjectedRemaining(strong, 2.5, .5), 8);
        Assert.Equal(.5, effects.ProjectedRemaining(mana, 2.5, .5), 8);
        Assert.Equal(0, effects.ProjectedRemaining(independent, 2.5, .5));
        Assert.Equal(0, effects.ProjectedRemaining(weak with { }, 2.5, .5)); // Original source identity only.
        Assert.Equal(3.5, effects.Remaining(weak));
        Assert.Equal(0, effects.ProjectedRemaining(mana, 100, .5));
        var actual = effects.Advance(.5, 2.5, manaRecoveryRate: 1.5, expiryRate: .5);
        Assert.Equal(55, actual.Life);
        Assert.Equal(60, actual.Mana);
        Assert.Same(independent, Assert.Single(actual.Ended));
        Assert.Equal(.5, effects.ProjectedRemaining(mana, 2.5, .5), 8); // No projection watermark was advanced to100.
        var rest = effects.Advance(2.5, 100, manaRecoveryRate: 1.5, expiryRate: .5);
        Assert.Equal(80, rest.Life);
        Assert.Equal(47, rest.Mana);
        Assert.Equal(3, rest.Ended.Count);
        Assert.Equal(0, effects.Advance(100, 101).Mana);
    }
}
