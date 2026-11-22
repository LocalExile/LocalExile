using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class LockstepCycloneTests
{
    const ushort Skill = 0x010e, Sequence = 0x8001;

    [Fact]
    public async Task Repeating_the_held_use_keeps_the_channel_and_steers_on_the_next_frame()
    {
        var conn = MovingChannel();
        using var wire = new PartyFollowerTests.Wire();

        var use = new LoginWriter().U16(InstanceProtocol.LockstepMoveStartId)
            .U32(30).U32(5).U16(Skill).U16(0x0408).U8(0).ToArray();
        Game.OnLockstepRequest("test", conn, InstanceProtocol.LockstepMoveStartId, use);
        Assert.Equal(Sequence, conn.LockstepSkill.Channel);
        Assert.False(conn.LockstepSkill.Pending);
        Assert.Equal((100, 100), conn.Walk.Destination);
        Assert.True(Game.ChannelCurrent(conn, Skill, action: 0, generation: 0));

        await Game.StartLockstepActions("test", wire.Stream, wire.Ciphers, conn, 1);
        Assert.Equal((130, 105), conn.Walk.Destination);
        Assert.Equal(Sequence, conn.ChannelSkillSequence);
        Assert.Equal(0, conn.ActionCount);
        Assert.Equal(0, conn.ChannelGeneration);
    }

    [Fact]
    public async Task Cursor_updates_coalesce_and_releasing_stops_in_place()
    {
        var conn = MovingChannel();
        using var wire = new PartyFollowerTests.Wire();

        Aim(30, 0);
        await Game.StartLockstepActions("test", wire.Stream, wire.Ciphers, conn, 1);
        Assert.Equal((130, 100), conn.Walk.Destination);
        Aim(0, 40);
        Aim(0, 50);
        await Game.StartLockstepActions("test", wire.Stream, wire.Ciphers, conn, 1.033);
        Assert.Equal((130, 100), conn.Walk.Destination);
        await Game.StartLockstepActions("test", wire.Stream, wire.Ciphers, conn, 1.066);
        Assert.Equal((100, 150), conn.Walk.Destination);

        Aim(30, 30);
        Game.OnLockstepRequest("test", conn, InstanceProtocol.LockstepMoveStopId,
            new LoginWriter().U16(InstanceProtocol.LockstepMoveStopId).ToArray());
        await Game.StartLockstepActions("test", wire.Stream, wire.Ciphers, conn, 1.1);
        Assert.Equal((ushort)0, conn.ChannelSkill);
        Assert.False(conn.Walk.Moving(1.2));
        Assert.Equal(conn.Walk.Position(1.1), conn.Walk.Destination);
        Assert.False(Game.ChannelCurrent(conn, Skill, action: 0, generation: 0));

        void Aim(int dx, int dy) => Game.OnLockstepRequest("test", conn, InstanceProtocol.LockstepMoveUpdateId,
            new LoginWriter().U16(InstanceProtocol.LockstepMoveUpdateId).U32((uint)dx).U32((uint)dy).ToArray());
    }

    static ClientState MovingChannel()
    {
        var conn = new ClientState(new(1, "cyclone-lockstep", 400, 400, 100, 100, null, []), 1, 1)
        {
            LockstepClient = true,
            PlayerLife = 100,
            ChannelSkill = Skill,
            ChannelSkillSequence = Sequence,
            ChannelMovement = (Skill, -30),
            LastChannelUpdateAt = -10,
        };
        conn.ApplyMovementSpeedAt(0);
        conn.LockstepSkill.Press((100, 100), null, Skill, 0x0408, repeats: false);
        Assert.True(conn.LockstepSkill.Due(free: true));
        conn.LockstepSkill.Channelling(Sequence);
        return conn;
    }
}
