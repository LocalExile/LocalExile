using GameServer.Modules.Atlas;

namespace GameServer.Gateway.Tests;

public sealed class MavenProgressTests
{
    [Fact]
    public void InvitationsRequireDistinctEligibleBossesAndAWinBeforeTheNextRound()
    {
        var rounds = MavenProgress.Rounds;
        Assert.Equal([6, 8, 10, 12, 14], rounds.Select(r => r.MapTier));
        Assert.Equal([3, 4, 5, 6, 10], rounds.Select(r => r.Count));
        var empty = new MavenProgress.Progress(0, []);
        Assert.False(MavenProgress.Ready(empty, rounds[0]));
        Assert.Same(empty, MavenProgress.Capture(empty, "A", "BossA", 5));
        var first = MavenProgress.Capture(empty, "A", "BossA", 6);
        Assert.Same(first, MavenProgress.Capture(first, "A", "OtherForm", 7));
        Assert.Same(first, MavenProgress.Capture(first, "B", "BossA", 7));
        var second = MavenProgress.Capture(first, "B", "BossB", 6);
        var ready = MavenProgress.Capture(second, "C", "BossC", 6);
        Assert.True(MavenProgress.Ready(ready, rounds[0]));
        Assert.False(MavenProgress.Ready(ready, rounds[1]));
        Assert.Same(ready, MavenProgress.Win(ready, 0, ["BossA", "BossB", "WrongBoss"]));
        var next = MavenProgress.Win(ready, 0, ["BossA", "BossB", "BossC"]);
        Assert.Equal(1, next.Wins);
        Assert.Empty(next.Witnesses);
        Assert.Equal(rounds[1], MavenProgress.Current(next));
        Assert.Same(next, MavenProgress.Win(next, 0, ["BossA", "BossB", "BossC"]));

        for (int roundIndex = 1; roundIndex < rounds.Count; roundIndex++)
        {
            var round = rounds[roundIndex];
            Assert.Same(next, MavenProgress.Capture(next, "A", "BossA", round.MapTier - 1));
            for (int i = 0; i < round.Count; i++)
                next = MavenProgress.Capture(next, $"Map{i}", $"Boss{i}", round.MapTier);
            Assert.True(MavenProgress.Ready(next, round));
            next = MavenProgress.Win(next, roundIndex, next.Witnesses.Select(w => w.Boss).ToArray());
        }
        Assert.Equal(rounds.Count, next.Wins);
        Assert.Equal(rounds[^1], MavenProgress.Current(next));
        Assert.False(MavenProgress.Ready(next, rounds[^1]));
    }
}
