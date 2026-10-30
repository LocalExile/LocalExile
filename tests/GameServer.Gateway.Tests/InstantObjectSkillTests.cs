using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class InstantObjectSkillTests
{
    static int NoStats(string _) => 0;
    internal static ClientState Connection() => new(new AreaLayout(1, "test", 400, 400, 280, 220, (_, _) => true, []), 1, 1)
        { PlayerSent = true, PlayerLife = 100, PlayerMana = 100, ActionCount = 17, ChannelSkill = 0x104,
            ChannelSkillSequence = 3, NextAttackAt = 20 };

    internal static SpawnedMonster Target(ClientState conn, int x = 300, int y = 220, int size = 3)
    {
        var target = new SpawnedMonster("target", new(x, y, 1, width: 400, height: 400), 0, 4, 100)
            { Variety = new(0, "target", "target", "base", null, null, ObjectSize: size) };
        conn.Monsters.Add(42, target);
        return target;
    }

    [Theory]
    [InlineData(300, 220, 312, 220, 317, 220, 317, 220)]
    public void Size_aware_trace_and_outer_clamp_match_manual_native_object_confirmations(
        int x, int y, int sx, int sy, int ex, int ey, int finalX, int finalY)
    {
        var trace = Assert.IsType<((int X, int Y) Start, (int X, int Y) End)>(
            InstantLocationSkill.ObjectAim((280, 220), (x, y), 1, 2));
        Assert.Equal(((sx, sy), (ex, ey)), trace);
        var conn = Connection(); Target(conn, x, y);
        Assert.True(InstantLocationSkill.TryResolveObject(conn, conn.ProjectileEpoch, 42, false, NoStats,
            out var from, out var to, out var aim));
        Assert.Equal((280, 220), from); Assert.Equal((x, y), aim); Assert.Equal((finalX, finalY), to);
        Assert.Equal((280, 220), conn.Walk.Destination);
        Assert.Equal(17, conn.ActionCount); Assert.Equal((ushort)0x104, conn.ChannelSkill);
        Assert.Equal(100u, conn.PlayerMana); Assert.Empty(conn.SkillReadyAt);
    }

}
