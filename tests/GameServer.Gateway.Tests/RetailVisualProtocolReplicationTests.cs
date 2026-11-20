using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class RetailVisualProtocolReplicationTests
{
    [Fact]
    public void Gateway_positioned_fixed_height_blobs_encode_negative_world_z_marker()
    {
        const float z = -117.1875f; // world z points down -> i32 marker = -z / 7.8125 = +15
        const int expectedMarker = 15;
        static void AssertFixedHeight(byte[] blob, int markerOffset)
        {
            Assert.Equal(expectedMarker, BitConverter.ToInt32(blob, markerOffset));
            Assert.Equal(z, BitConverter.ToSingle(blob, markerOffset + 4));
        }

        // Object header (2) + Positioned x/y/rot/team/f0/f1 (16) = offset 18.
        var placement = new GlyphQuest.Placement(GlyphQuest.Wall, "Metadata/QuestObjects/Act1/GlyphWall.ao", 10, 20, 0, z, 0);
        AssertFixedHeight(GlyphQuest.WallBlob(placement, placed: false), 18);
        AssertFixedHeight(LabyrinthTrials.SpikeTrapBlob(10, 20, height: z), 18);
        AssertFixedHeight(LabyrinthTrials.Blob(["Positioned"], "", 10, 20, 0, 1, 1, z, blockage: false, blocked: false), 18);
        AssertFixedHeight(new TransitionEffect("Metadata/Effects/GlowyFX", "Metadata/Effects/GlowyFX.ao", 10, 20, 0, z).Blob(), 18);
        AssertFixedHeight(MapSulphite.Blob(10, 20, z, mined: false, fumes: false), 18);
    }

    [Fact]
    public async Task Player_degen_death_sends_death_from_degen_kind_while_living_updates_stay_zero()
    {
        var conn = new ClientState(new(1, "degen-death", 300, 300, 100, 100, null, []), 1, 1) { PlayerLife = 50 };
        Assert.Equal(InstanceProtocol.DeathKindNormal, Game.PlayerLifeUpdate(conn, InstanceProtocol.DeathKindDegen)[^1]);

        conn.PlayerLife = 0;
        Assert.Equal(InstanceProtocol.DeathKindNormal, Game.PlayerLifeUpdate(conn)[^1]);
        Assert.Equal(InstanceProtocol.DeathKindDegen, Game.PlayerLifeUpdate(conn, InstanceProtocol.DeathKindDegen)[^1]);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accept;
        listener.Stop();

        var key = new byte[64];
        var serverCiphers = new ConnectionCiphers(key, isClient: false);
        var clientCiphers = new ConnectionCiphers(key, isClient: true);
        try
        {
            await Game.SendPlayerDeathPresentation("test", server.GetStream(), serverCiphers, conn,
                "degen death", conn.IncomingHitEpoch, InstanceProtocol.DeathKindDegen);
            byte[] expectedLife = InstanceProtocol.LifeUpdate(Program.PlayerId, 0, conn.PlayerReservedLife,
                u2c: InstanceProtocol.DeathKindDegen);
            byte[] expectedStats = InstanceEntities.StatsUpdate(Program.PlayerId, [(StatKeys.HideMiniLifeBar, 1)]);
            byte[] expectedScreen = InstanceProtocol.DeathScreen(Game.DeathScreenFlags(conn));
            byte[] expected = [.. expectedLife, .. expectedStats, .. expectedScreen];
            byte[] got = new byte[expected.Length];
            await client.GetStream().ReadExactlyAsync(got);
            clientCiphers.Receive.Process(got);
            Assert.Equal(expected, got);
        }
        finally { conn.Closed = true; }
    }

    [Fact]
    public async Task Monster_poison_kill_sends_death_from_degen_life_update()
    {
        var previous = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accept;
        listener.Stop();

        var key = new byte[64];
        var serverCiphers = new ConnectionCiphers(key, isClient: false);
        var clientCiphers = new ConnectionCiphers(key, isClient: true);
        var conn = new ClientState(new(1, "poison-death", 300, 300, 100, 100, null, []), 1, 1) { Instance = new() };
        var stream = server.GetStream();
        conn.Wire = ("test", stream, serverCiphers);
        var received = new MemoryStream();
        var reader = Task.Run(async () =>
        {
            var buf = new byte[4096];
            int n;
            while ((n = await client.GetStream().ReadAsync(buf)) > 0)
            {
                clientCiphers.Receive.Process(buf.AsSpan(0, n));
                received.Write(buf, 0, n);
            }
        });

        try
        {
            const uint monsterId = 0x3055;
            var variety = new MonsterCatalog().Find("Metadata/Monsters/Zombies/Zombie")!;
            var monster = new SpawnedMonster("Zombie", new(100, 100, 1), 100, 10, 5)
            {
                Hostile = true,
                Variety = variety,
                Level = 1,
            };
            conn.Monsters[monsterId] = monster;
            // Apply poison starting at t = -1.0s so the current world time (~0s) immediately advances a full lethal second.
            Assert.True(monster.Poisons.Add(-1.0, 100, 5));

            await Game.TickPoisons("test", stream, serverCiphers, conn);
            Assert.Equal(0u, monster.Life);
            Assert.True(monster.Dead);

            server.Client.Shutdown(SocketShutdown.Send);
            await reader;

            byte[] expectedDeath = InstanceProtocol.LifeUpdate(monsterId, 0, u2c: InstanceProtocol.DeathKindDegen);
            Assert.Contains(Convert.ToHexString(expectedDeath), Convert.ToHexString(received.ToArray()));
        }
        finally
        {
            conn.Closed = true;
            Program.SetGameLogic(previous!);
        }
    }
}
