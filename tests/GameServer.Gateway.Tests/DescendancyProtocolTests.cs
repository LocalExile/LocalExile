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
        var conn = new ClientState(new(1, "descendancy-admission", 100, 100, 50, 50, null, []), 0, 1);
        Assert.False(Game.CanSelectDescendancy(conn, 6, out _));
        conn.OfferedDescendancyRow = 6;
        conn.OfferedDescendancyArea = conn.Layout.Hash;
        Assert.True(Game.CanSelectDescendancy(conn, 6, out _));
        Assert.True(Game.CanSelectDescendancy(conn, 5, out _)); // the open selector may show another row
        conn.OfferedDescendancyArea = 2;
        Assert.False(Game.CanSelectDescendancy(conn, 6, out _));

        // An allocated layer must be refunded before another row replaces it, and the Wildwood rows need their gate.
        conn.OfferedDescendancyArea = conn.Layout.Hash;
        conn.DescendancyRow = 6;
        conn.DescendancyNodes.Add(63623);
        conn.OfferedDescendancyRow = 5;
        Assert.False(Game.CanSelectDescendancy(conn, 5, out _));
        conn.DescendancyRow = 0;
        conn.DescendancyNodes.Clear();
        conn.OfferedDescendancyRow = 3;
        Assert.False(Game.CanSelectDescendancy(conn, 3, out _));
        conn.QuestFlags.Add("AzmeriArePrimalist");
        Assert.True(Game.CanSelectDescendancy(conn, 3, out _));
    }
}
