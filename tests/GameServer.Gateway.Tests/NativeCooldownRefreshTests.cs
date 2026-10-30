using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class NativeCooldownRefreshTests
{
    static readonly (ushort Id, int Effect)[] Eligible = [(0x103, 10619)];
    static (ClientState Conn, SingleUseCooldownState State, CooldownUseTicket Ticket) Spent()
    {
        var conn = new ClientState(new AreaLayout(1, "native-cooldown", 200, 200, 10, 10, null, []), 1, 1)
            { PlayerLife = 100, PlayerMana = 100, ClientSkillIds = [0x103] };
        var state = new SingleUseCooldownState(10, 1);
        Assert.True(state.TryUse(10, new(2500, 0)));
        conn.SingleUseCooldowns[10619] = state;
        return (conn, state, new(10619, conn.ActionCount, conn.ProjectileEpoch, false, State: state, UseVersion: state.UseVersion));
    }
    static byte[]? Snapshot(ClientState conn) => Program.NativeCooldownRefresh(conn, conn.ProjectileEpoch, () => Eligible);
    static float Float(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));

    [Fact]
    public void An_acknowledged_active_rate_change_sends_progress_once_without_paying_or_consuming_again()
    {
        var (conn, state, ticket) = Spent();
        Assert.True(ticket.CommitNativeUse(conn, 0x103));
        state.Refresh(10.5, 2);
        byte[] bytes = Assert.IsType<byte[]>(Snapshot(conn));
        Assert.Equal(2, Float(bytes, 29));
        Assert.Equal(2.5f, Float(bytes, 33));
        Assert.Equal(2, Float(bytes, 37));
        Assert.Equal(100u, conn.PlayerMana);
        Assert.Equal(0, conn.ActionCount);
        Assert.True(ticket.TryExecute(conn, 10619));
        Assert.False(ticket.TryExecute(conn, 10619));
        Assert.Null(Snapshot(conn));
    }

}
