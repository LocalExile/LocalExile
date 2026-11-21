using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DescendancyProtocolTests
{
    [Fact]
    public void The_passive_tree_carries_the_descendancy_row_before_its_node_lists()
    {
        byte[] message = InstanceEntities.CharacterPassives(90, 0x01, [0x2a, 0x29], descendancy: 6);
        Assert.Equal(InstanceEntities.CharacterPassivesId, (ushort)(message[0] << 8 | message[1]));
        Assert.Equal(90, message[8]);
        Assert.Equal(0x01, message[9]);
        Assert.Equal(6, message[10]);
        Assert.Equal(0, message[11]);
    }

    [Fact]
    public void The_descendancy_accept_is_fixed_length_and_needs_a_server_offer()
    {
        Assert.Equal(InstanceWaypoints.DescendancyChosenLength,
            InstanceProtocol.KnownClientLength(InstanceWaypoints.DescendancyChosenId));

        // A client cannot select without a selector the server opened in this area.
        var player = new Player(new(1, "descendancy-admission", 100, 100, 50, 50, null, []), 0, 1);
        Assert.False(Game.CanSelectDescendancy(player, 6, out _));
        player.Character.OfferedDescendancyRow = 6;
        player.Character.OfferedDescendancyArea = player.Layout.Hash;
        Assert.True(Game.CanSelectDescendancy(player, 6, out _));
        Assert.True(Game.CanSelectDescendancy(player, 5, out _)); // the open selector may show another row
        player.Character.OfferedDescendancyArea = 2;
        Assert.False(Game.CanSelectDescendancy(player, 6, out _));

        // An allocated layer must be refunded before another row replaces it, and the Wildwood rows need their gate.
        player.Character.OfferedDescendancyArea = player.Layout.Hash;
        player.Character.DescendancyRow = 6;
        player.Character.DescendancyNodes.Add(63623);
        player.Character.OfferedDescendancyRow = 5;
        Assert.False(Game.CanSelectDescendancy(player, 5, out _));
        player.Character.DescendancyRow = 0;
        player.Character.DescendancyNodes.Clear();
        player.Character.OfferedDescendancyRow = 3;
        Assert.False(Game.CanSelectDescendancy(player, 3, out _));
        player.Character.QuestFlags.Add("AzmeriArePrimalist");
        Assert.True(Game.CanSelectDescendancy(player, 3, out _));
    }
}
