using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modules.Strongbox;

namespace GameServer.Gateway.Tests;

public sealed class StrongboxTests
{
    // An opened box's loot waits for its guards, and any connection in the instance releases it, exactly once: the opener
    // may have left (a portal out mid-fight), and a party member or the player back on a new connection ticks instead;
    // two connections seeing the guards dead in the same tick still drop it once. Armourer's (Strongboxes row 2) drops
    // 15 unstackable items with no mods or area stats, so one release is exactly 15 ground items.
    [Fact]
    public async Task Pending_loot_drops_once_from_any_connection_in_the_instance()
    {
        const uint Guard = 0x7101, OtherGuard = 0x7102;
        var instance = new InstanceState();
        instance.ContentState["Strongbox/plan"] =
            $$"""[{"Row":2,"X":100,"Y":100,"Rotation":0,"Id":67108864,"Opened":true,"Guards":[{{Guard}}],"Pending":true},""" +
            $$"""{"Row":2,"X":140,"Y":100,"Rotation":0,"Id":67108865,"Opened":true,"Guards":[{{OtherGuard}}],"Pending":true}]""";
        await using var opener = await Wire.Open(instance);
        await using var member = await Wire.Open(instance);
        var guard = new SpawnedMonster("guard", new(100, 105, 10, (_, _) => true, 1000, 1000), 20, 5, 1000);
        var other = new SpawnedMonster("guard", new(140, 105, 10, (_, _) => true, 1000, 1000), 20, 5, 1000);
        lock (instance.World.Monsters) { instance.World.Monsters[Guard] = guard; instance.World.Monsters[OtherGuard] = other; }
        int Ground() => instance.Ground.All().Count;

        await StrongboxManager.Tick(member.Conn);
        Assert.Equal(0, Ground());                     // a guard lives: nothing yet
        opener.Conn.LeavingArea = true;                // the opener portals out
        guard.Dead = true;
        await StrongboxManager.Tick(opener.Conn);      // a leaving connection does not tick it
        Assert.Equal(0, Ground());
        await StrongboxManager.Tick(member.Conn);      // the member does, though it never populated the instance
        Assert.Equal(15, Ground());
        await StrongboxManager.Tick(member.Conn);
        Assert.Equal(15, Ground());

        opener.Conn.LeavingArea = false;
        other.Dead = true;
        await Task.WhenAll(StrongboxManager.Tick(opener.Conn), StrongboxManager.Tick(member.Conn));
        Assert.Equal(30, Ground());
        Assert.Contains("\"Looted\":true", instance.ContentState["Strongbox/plan"]);
        Assert.DoesNotContain("\"Pending\":true", instance.ContentState["Strongbox/plan"]);
    }

    sealed class Wire(TcpClient writer, TcpClient reader, ClientState conn, Task drain) : IAsyncDisposable
    {
        public ClientState Conn => conn;
        public static async Task<Wire> Open(InstanceState instance)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var conn = new ClientState(new(0, "StrongboxTest", 1000, 1000, 100, 100, (_, _) => true, []), 0, 1)
            {
                Instance = instance, PlayerLife = 1000, PlayerSent = true,
                LeagueModule = new(new GameServer.Core.Accounts.League("strongbox-test", "Standard", false, false),
                    new HashSet<string>(["Game", "Strongbox"])),
                Wire = ("strongbox-regression", writer.GetStream(), new ConnectionCiphers(new byte[64], false)),
            };
            return new(writer, reader, conn, reader.GetStream().CopyToAsync(Stream.Null));
        }
        public async ValueTask DisposeAsync()
        {
            conn.Closed = true;
            writer.Dispose();
            await drain;
            reader.Dispose();
        }
    }
}
