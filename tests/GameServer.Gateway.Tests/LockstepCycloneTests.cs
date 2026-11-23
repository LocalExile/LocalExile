using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class LockstepCycloneTests
{
    const ushort Skill = 0x010e, Sequence = 0x8001;

    [Fact]
    public async Task Repeating_the_held_use_keeps_the_channel_and_steers_on_the_next_frame()
    {
        var player = MovingChannel();
        using var wire = new PartyFollowerTests.Wire();
        player.Connection.Session = new("test", wire.Stream, wire.Ciphers);

        var use = new LoginWriter().U16(InstanceProtocol.LockstepMoveStartId)
            .U32(30).U32(5).U16(Skill).U16(0x0408).U8(0).ToArray();
        Game.OnLockstepRequest(player, InstanceProtocol.LockstepMoveStartId, use);
        Assert.Equal(Sequence, player.LockstepSkill.Channel);
        Assert.False(player.LockstepSkill.Pending);
        Assert.Equal((100, 100), player.Walk.Destination);
        Assert.True(Game.ChannelCurrent(player, Skill, action: 0, generation: 0));

        await Game.StartLockstepActions(player, 1);
        Assert.Equal((130, 105), player.Walk.Destination);
        Assert.Equal(Sequence, player.ChannelSkillSequence);
        Assert.Equal(0, player.ActionCount);
        Assert.Equal(0, player.ChannelGeneration);
    }

    [Fact]
    public async Task Cursor_updates_coalesce_and_releasing_stops_in_place()
    {
        var player = MovingChannel();
        using var wire = new PartyFollowerTests.Wire();
        player.Connection.Session = new("test", wire.Stream, wire.Ciphers);

        Aim(30, 0);
        await Game.StartLockstepActions(player, 1);
        Assert.Equal((130, 100), player.Walk.Destination);
        Aim(0, 40);
        Aim(0, 50);
        await Game.StartLockstepActions(player, 1.033);
        Assert.Equal((130, 100), player.Walk.Destination);
        await Game.StartLockstepActions(player, 1.066);
        Assert.Equal((100, 150), player.Walk.Destination);

        Aim(30, 30);
        Game.OnLockstepRequest(player, InstanceProtocol.LockstepMoveStopId,
            new LoginWriter().U16(InstanceProtocol.LockstepMoveStopId).ToArray());
        await Game.StartLockstepActions(player, 1.1);
        Assert.Equal((ushort)0, player.ChannelSkill);
        Assert.False(player.Walk.Moving(1.2));
        Assert.Equal(player.Walk.Position(1.1), player.Walk.Destination);
        Assert.False(Game.ChannelCurrent(player, Skill, action: 0, generation: 0));

        void Aim(int dx, int dy) => Game.OnLockstepRequest(player, InstanceProtocol.LockstepMoveUpdateId,
            new LoginWriter().U16(InstanceProtocol.LockstepMoveUpdateId).U32((uint)dx).U32((uint)dy).ToArray());
    }

    static Player MovingChannel()
    {
        var player = new Player(new(1, "cyclone-lockstep", 400, 400, 100, 100, null, []), 1, 1)
        {
            Connection = { LockstepClient = true },
            PlayerLife = 100,
            ChannelSkill = Skill,
            ChannelSkillSequence = Sequence,
            ChannelMovement = (Skill, -30),
            LastChannelUpdateAt = -10,
        };
        player.ApplyMovementSpeedAt(0);
        player.LockstepSkill.Press((100, 100), null, Skill, 0x0408, repeats: false);
        Assert.True(player.LockstepSkill.Due(free: true));
        player.LockstepSkill.Channelling(Sequence);
        return player;
    }
}
