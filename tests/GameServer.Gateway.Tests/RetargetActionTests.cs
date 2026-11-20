using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class RetargetActionTests : IDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");

    public RetargetActionTests()
    {
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
    }

    public void Dispose()
    {
        lifetime.Stop(ex => throw ex);
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previousLogic!);
    }
    static byte[] RetargetPacket(uint targetId) =>
        new LoginWriter().U16(InstanceProtocol.RetargetActionId).U32(targetId).ToArray();

    static (NetworkStream Server, NetworkStream Client, ConnectionCiphers ServerCiphers, ConnectionCiphers ClientCiphers, IDisposable Cleanup) Loopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = accept.GetAwaiter().GetResult();
        listener.Stop();
        byte[] key = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
        return (server.GetStream(), client.GetStream(),
            new ConnectionCiphers(key, isClient: false), new ConnectionCiphers(key, isClient: true),
            new Scope(() => { server.Dispose(); client.Dispose(); }));
    }

    sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    [Fact]
    public async Task OnClientData_frames_repeated_retarget_packets_without_tripping_unframed_disconnect()
    {
        var (serverStream, _, serverCiphers, clientCiphers, cleanup) = Loopback();
        using var _ = cleanup;
        var conn = new ClientState(new(1, "retarget-framing", 400, 400, 100, 100, null, []), 1, 1);
        var inbox = new ClientInbox(conn.Performance);

        for (int i = 0; i < ClientInbox.LostAfterUnframedReads + 5; i++)
        {
            byte[] wire = RetargetPacket(0x3000u + (uint)i);
            clientCiphers.Send.Process(wire);
            await Program.OnClientData("retarget-test", serverStream, serverCiphers, wire, conn, inbox, readAt: 0);
            Assert.Equal(0, conn.UnframedRun);
            Assert.Null(conn.CloseReason);
        }

        // Coalesced 0x0193 + 0x0197 in a single read preserves both framed packets.
        byte[] coalesced = [.. RetargetPacket(0x3042), .. new LoginWriter().U16(InstanceProtocol.ChannelUpdateId).U32(150).U32(120).ToArray()];
        clientCiphers.Send.Process(coalesced);
        await Program.OnClientData("retarget-test", serverStream, serverCiphers, coalesced, conn, inbox, readAt: 0);
        Assert.Equal(0, conn.UnframedRun);
        Assert.Null(conn.CloseReason);

        inbox.Complete();
        var batches = new List<ClientInbox.Batch>();
        await foreach (var batch in inbox.ReadAllAsync()) batches.Add(batch);
        Assert.Equal(ClientInbox.LostAfterUnframedReads + 6, batches.Count);
        Assert.All(batches, b => Assert.Empty(b.Unknown));
        var last = batches[^1];
        Assert.Equal(new ushort[] { InstanceProtocol.RetargetActionId, InstanceProtocol.ChannelUpdateId },
            last.Messages.Select(m => m.Id));
        Assert.Equal(0x3042u, InstanceProtocol.ReadRetargetAction(last.Messages[0].Bytes));
    }

    [Fact]
    public async Task Predictive_channel_retargets_toward_living_monster_position_and_refreshes_activity()
    {
        var (serverStream, _, serverCiphers, _, cleanup) = Loopback();
        using var _ = cleanup;
        var conn = new ClientState(new(1, "retarget-predictive", 400, 400, 100, 100, null, []), 1, 1);
        conn.ChannelSkill = 0x010c;
        conn.ChannelMovement = (0x010c, -28);
        conn.LastChannelUpdateAt = -10;
        conn.Monsters[0x3001] = new SpawnedMonster("target", new PlayerWalk(145, 112, 0), 0, 0, 1000) { Hostile = true };

        byte[] packet = RetargetPacket(0x3001);
        await Game.OnClientMessages("retarget-pred", serverStream, serverCiphers, [(InstanceProtocol.RetargetActionId, packet)], [], conn);

        Assert.False(conn.LockstepClient);
        // Living monster collision keeps the walk destination just outside the target's body footprint.
        Assert.Equal((141, 112), conn.Walk.Destination);
        Assert.True(conn.LastChannelUpdateAt >= 0);
    }

    [Fact]
    public async Task Lockstep_channel_retargets_aim_emits_action_target_and_refreshes_activity()
    {
        var (serverStream, clientStream, serverCiphers, clientCiphers, cleanup) = Loopback();
        using var _ = cleanup;
        var conn = new ClientState(new(1, "retarget-lockstep", 400, 400, 100, 100, null, []), 1, 1)
        {
            LockstepClient = true,
            ChannelSkill = 0x0200,
            ChannelSkillSequence = 0x8001,
            ChannelMovement = (0x0200, -28),
            LastChannelUpdateAt = -10,
        };
        conn.LockstepSkill.Press((105, 100), null, 0x0200, 0x0401, repeats: false);
        conn.LockstepSkill.Channelling(0x8001);
        conn.Monsters[0x3005] = new SpawnedMonster("boss", new PlayerWalk(132, 118, 0), 0, 0, 1000) { Hostile = true };

        byte[] packet = RetargetPacket(0x3005);
        await Game.OnClientMessages("retarget-ls", serverStream, serverCiphers, [(InstanceProtocol.RetargetActionId, packet)], [], conn);

        Assert.Equal((132, 118), conn.LockstepSkill.Aim);
        Assert.Equal(0x3005u, conn.LockstepSkill.Target);
        Assert.Equal((128, 118), conn.Walk.Destination);
        Assert.True(conn.LastChannelUpdateAt >= 0);

        byte[] expected = InstanceProtocol.ActionTarget(Program.PlayerId, 0x8001, 132, 118);
        byte[] received = new byte[expected.Length];
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await clientStream.ReadExactlyAsync(received, readTimeout.Token);
        clientCiphers.Receive.Process(received);
        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task Lockstep_held_non_channel_skill_and_move_update_aim_to_monster_position()
    {
        var (serverStream, _, serverCiphers, _, cleanup) = Loopback();
        using var _ = cleanup;
        var conn = new ClientState(new(1, "retarget-held", 400, 400, 100, 100, null, []), 1, 1)
        {
            LockstepClient = true,
        };
        conn.LockstepSkill.Press((105, 100), null, 0x0102, 0x0401, repeats: true);
        conn.Monsters[0x3009] = new SpawnedMonster("mob", new PlayerWalk(122, 95, 0), 0, 0, 500) { Hostile = true };

        await Game.OnClientMessages("retarget-held", serverStream, serverCiphers,
            [(InstanceProtocol.RetargetActionId, RetargetPacket(0x3009))], [], conn);

        Assert.Equal((122, 95), conn.LockstepSkill.Aim);
        Assert.Equal(0x3009u, conn.LockstepSkill.Target);

        conn.LockstepSkill.Clear();
        conn.LockstepHold.Press((105, 100), 0x2909, 0x0400);
        Assert.Equal((105, 100), conn.LockstepHold.Due(walking: true, here: (100, 100)));

        await Game.OnClientMessages("retarget-move", serverStream, serverCiphers,
            [(InstanceProtocol.RetargetActionId, RetargetPacket(0x3009))], [], conn);

        Assert.Equal((122, 95), conn.LockstepHold.Due(walking: true, here: (100, 100)));
    }

    [Fact]
    public async Task Retarget_safely_ignores_unknown_dead_or_idle_states()
    {
        var (serverStream, _, serverCiphers, _, cleanup) = Loopback();
        using var _ = cleanup;
        var conn = new ClientState(new(1, "retarget-ignore", 400, 400, 100, 100, null, []), 1, 1);
        conn.Monsters[0x3010] = new SpawnedMonster("alive", new PlayerWalk(140, 140, 0), 0, 0, 500) { Hostile = true };
        conn.Monsters[0x3011] = new SpawnedMonster("dead", new PlayerWalk(160, 160, 0), 0, 0, 500) { Hostile = true, Life = 0, Dead = true };

        // Idle player with living target: no movement or channel activity change.
        conn.LastChannelUpdateAt = -10;
        await Game.OnClientMessages("retarget-idle", serverStream, serverCiphers,
            [(InstanceProtocol.RetargetActionId, RetargetPacket(0x3010))], [], conn);
        Assert.Equal((100, 100), conn.Walk.Destination);
        Assert.Equal(-10, conn.LastChannelUpdateAt);
        Assert.Null(conn.CloseReason);

        // Active channel with unknown target id: ignored safely.
        conn.ChannelSkill = 0x010c;
        conn.ChannelMovement = (0x010c, -28);
        await Game.OnClientMessages("retarget-unknown", serverStream, serverCiphers,
            [(InstanceProtocol.RetargetActionId, RetargetPacket(0x9999))], [], conn);
        Assert.Equal((100, 100), conn.Walk.Destination);
        Assert.Equal(-10, conn.LastChannelUpdateAt);
        Assert.Null(conn.CloseReason);

        // Active channel with dead target: ignored safely.
        await Game.OnClientMessages("retarget-dead", serverStream, serverCiphers,
            [(InstanceProtocol.RetargetActionId, RetargetPacket(0x3011))], [], conn);
        Assert.Equal((100, 100), conn.Walk.Destination);
        Assert.Equal(-10, conn.LastChannelUpdateAt);
        Assert.Null(conn.CloseReason);
    }
}
