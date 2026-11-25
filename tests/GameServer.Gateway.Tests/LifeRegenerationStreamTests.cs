using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The client predicts its Life from the life recovery rate it was sent, so the server writes no Life update (S→C 0x1b1,
// pool 0) for the frames of a plain regeneration; it writes one for what the client cannot predict.
public sealed class LifeRegenerationStreamTests
{
    sealed class Clock : IPlayerUpdateSchedule
    {
        public double Time = 10;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.Delay(Timeout.Infinite, stopping);
    }

    // Steps the area's frames with the player's updates running on them, as the game does, at 100 Life regenerated a
    // second. For each frame: its Life lines on the wire and whether the player's Life moved. The frames up to the first
    // regeneration step (which sends the rates) and three after it are not reported.
    static async Task<(List<(int Lines, bool Moved, bool Leeching)> Frames, string Last, string Expected, double Seconds, int ManaLines)> Run(
        int frames, Action<Player, int, double>? during, bool manaBelowFull = false)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        using var source = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player)) stats[Stat.BaseLifeRegenerationRatePerMinute] = 6000;
            return stats;
        });
        var clock = new Clock();
        var previousSchedule = Game.PlayerUpdatesSchedule;
        var previousContext = Game.AreaUpdateContext.Value;
        Game.PlayerUpdatesSchedule = clock;
        try
        {
            player.Connection.PlayerObjectCreated = true;
            player.Instance.AddPlayer(player);
            Program.InGame.TryAdd(player, 0);
            player.Pools = Game.PoolsFor(player, 100);
            player.PlayerLife = player.PlayerMaxLife / 2;
            player.PlayerMana = manaBelowFull ? player.Pools.Mana / 2 : player.Pools.Mana;
            double frameSeconds = Program.FrameMs / 1000d;
            await Program.BeginWorldFrames(player, frameSeconds);
            var area = player.Area;
            long frame = 0;
            string[] Pool(string pool) => [.. Program.HistoryFor(fixture.Ciphers).Lines()
                .Select(line => line[(line.LastIndexOf(' ') + 1)..])
                .Where(hex => hex.StartsWith("01B1", StringComparison.Ordinal) && hex[40..42] == pool)];
            string[] Life() => Pool("00");
            async Task Step()
            {
                clock.Time += frameSeconds;
                var owner = GameArea.Own(area);
                try { area.Step(++frame, clock.Time); area.Life.Publish(); }
                finally { GameArea.Own(owner); }
                var delta = area.Delta.Take();
                for (int i = 0; i < 8; i++) await Task.Yield();
                Assert.True(await Program.EndWorldFrame(player, (clock.Time, clock.Time + frameSeconds), delta));
            }
            Game.EnsurePlayerUpdates(player);
            // The updates register their frame timers once started: step until Life moves.
            uint start = player.PlayerLife;
            for (int i = 0; i < 200 && player.PlayerLife == start; i++) await Step();
            Assert.True(player.PlayerLife > start, "no regeneration");
            for (int i = 0; i < 3; i++) await Step();
            var counts = new List<(int, bool, bool)>();
            int seen = Life().Length;
            double from = clock.Time;
            for (int i = 0; i < frames; i++)
            {
                uint before = player.PlayerLife;
                during?.Invoke(player, i, clock.Time);
                bool leeching = Game.LeechOf(player).Life.Count > 0;
                await Step();
                int now = Life().Length;
                counts.Add((now - seen, player.PlayerLife != before, leeching));
                seen = now;
            }
            return (counts, Life()[^1], Convert.ToHexString(InstanceProtocol.LifeUpdate(player.Id, player.PlayerLife, 0, orderKey: uint.MaxValue)),
                clock.Time - from, Pool("01").Length);
        }
        finally
        {
            player.RetirePlayerUpdates();
            player.Instance.RemovePlayer(player.Id);
            Program.InGame.TryRemove(player, out _);
            player.Closed = true;
            Game.PlayerUpdatesSchedule = previousSchedule;
            Game.AreaUpdateContext.Value = previousContext;
        }
    }

    [Fact]
    public async Task Plain_regeneration_writes_one_life_update_at_the_cap_and_none_before()
    {
        // About 500 Life to regenerate at 100 a second: 150 frames, then the cap. Mana is below full all the while, and it
        // is written every frame it moves without a Life line with it.
        var (frames, last, expected, seconds, manaLines) = await Run(200, null, manaBelowFull: true);
        Assert.True(frames.Count(f => f.Moved) > 100);
        Assert.Equal(1, frames.Sum(f => f.Lines));
        // The one is the cap's, in the last frame that moved Life.
        Assert.Equal(expected, last);
        int capFrame = frames.FindIndex(f => f.Lines == 1);
        Assert.DoesNotContain(frames.Skip(capFrame + 1), f => f.Moved);
        Assert.True(seconds > 5);
        Assert.True(manaLines > 20);
    }

    [Fact]
    public async Task A_hit_a_poison_and_a_source_the_client_cannot_predict_still_write_life()
    {
        var leechSources = new LeechSources(DamagePacket.Of(DamageType.Physical, 1000, 1000), DamagePacket.Empty,
            DamagePacket.Empty) { Attack = true };
        static int NoInstant(uint _) => 0;
        var (frames, _, _, _, _) = await Run(40, (player, i, now) =>
        {
            // Frames 0-9 regenerate; frame 10 loses 50 Life (what a hit does to it); the regeneration after it is quiet
            // again; from frame 30 a timed leech adds Life the client does not know of.
            if (i == 10) player.PlayerLife -= 50;
            if (i == 30)
            {
                Game.LeechOf(player).Configure(0, LeechLimits.Resolve(NoInstant, player.Pools.Life, player.Pools.Mana,
                    player.Pools.EnergyShield), NoInstant);
                Game.LeechOnHitLocked(player, new("leech", new(0, 0, 0), 0, 0, 1000) { Life = 1000 },
                    DamagePacket.Of(DamageType.Physical, 500, 500), 500, leechSources);
            }
        });
        // A stopped regeneration passes none of these: the quiet frames moved Life.
        Assert.All(frames.Take(10), f => { Assert.Equal(0, f.Lines); Assert.True(f.Moved); });
        Assert.Equal(1, frames[10].Lines);
        Assert.All(frames.Skip(11).Take(19), f => { Assert.Equal(0, f.Lines); Assert.True(f.Moved); });
        // While the leech runs, each frame that moved Life wrote it.
        var leeching = frames.Skip(31).Where(f => f.Leeching && f.Moved).ToList();
        Assert.True(leeching.Count >= 3);
        Assert.All(leeching, f => Assert.Equal(1, f.Lines));

        // A poison that lowers Life in the step that regenerates it (it lands after the regeneration is paid): each frame
        // that moved Life wrote it, though the regeneration alone outpaces the poison.
        var (poisoned, _, _, _, _) = await Run(30, (player, i, now) =>
        {
            if (i == 10) player.IncomingAilments.ApplyPoison(now, 60, 10);
        });
        Assert.All(poisoned.Take(10), f => { Assert.Equal(0, f.Lines); Assert.True(f.Moved); });
        Assert.All(poisoned.Skip(12), f => { Assert.Equal(1, f.Lines); Assert.True(f.Moved); });
    }
}
