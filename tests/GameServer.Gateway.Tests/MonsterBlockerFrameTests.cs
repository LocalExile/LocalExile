using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class MonsterBlockerFrameTests
{
    [Fact]
    public void All_movers_share_one_frame_with_fresh_updates_and_self_exclusion()
    {
        var conn = new ClientState(new(1, "test", 200, 200, 10, 10, (_, _) => true, []), 1, 1)
            { Instance = new(), PlayerSent = true, PlayerLife = 100 };
        conn.SetBlocker(9, 90, 30, 2);
        conn.Monsters[1] = new("self", new(30, 30, 1), 0, 4, 100) { CollisionPosition = (20, 30) };
        conn.Monsters[2] = new("other", new(60, 30, 1), 0, 4, 100) { CollisionPosition = (50, 30) };
        lock (conn.Monsters)
        {
            using (var frame = conn.BeginMonsterBlockerFrame(conn.Clock.Elapsed.TotalSeconds, includePrevious: true))
            {
                var current = conn.MonsterBlockers(1);
                var previous = conn.MonsterBlockers(1, includePrevious: true);
                for (int i = 0; i < 150; i++) Assert.Same(current, conn.MonsterBlockers(1));
                Assert.Equal(1, conn.Performance.Read(LatencyPhase.BlockerBuild).Count);
                Assert.False(ClientPathfinder.ObjectBlocks(previous, 20, 30, 2, 5));
                Assert.False(ClientPathfinder.ObjectBlocks(previous, 30, 30, 2, 5));
                Assert.False(ClientPathfinder.ObjectBlocks(current, 50, 30, 2, 5));
                Assert.True(ClientPathfinder.ObjectBlocks(previous, 50, 30, 2, 5));
                frame.Move(2, 70, 30); frame.CompleteCorrection(2, 70, 30);
                Assert.False(ClientPathfinder.ObjectBlocks(previous, 50, 30, 2, 5));
                Assert.False(ClientPathfinder.ObjectBlocks(previous, 60, 30, 2, 5));
                Assert.True(ClientPathfinder.ObjectBlocks(current, 70, 30, 2, 5));
            }
            conn.Monsters[2].Dead = true;
            Assert.False(ClientPathfinder.ObjectBlocks(conn.MonsterBlockers(1), 60, 30, 2, 5));
            Assert.Equal(2, conn.Performance.Read(LatencyPhase.BlockerBuild).Count);
        }
    }
}
