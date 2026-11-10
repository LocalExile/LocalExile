using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A spawned Shaper or Elder stood next to the player and did nothing at all (live 2026-11-10, area 1_1_2; the same
// spawn in MapWorldsShapersRealm looked like it worked, but its swings still never reached the player). The controller
// is driven in both places: it reaches Act and orders its first swing. One guard between the controller and the client
// then swallows every ordered swing while the player still has entry protection: Game.InGracePeriod in
// Monsters.MonsterSwing (Monsters.cs:1426), which the ordinary monsters' AI passes through as well. These two tests
// drive Step with a hand-set clock and a real socket, so the two halves are told apart.
public sealed class AtlasBossDriveTests
{
    const uint BossId = 0x80000d4;
    const string Shaper = "Metadata/Monsters/AtlasBosses/TheShaperBoss";

    [Fact]
    public async Task A_shaper_in_melee_range_puts_its_first_swing_on_the_wire()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accepting;

        var serverCiphers = new ConnectionCiphers(new byte[64], isClient: false);
        var clientCiphers = new ConnectionCiphers(new byte[64], isClient: true);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());

            var (conn, boss) = MeleeSetup(server.GetStream(), serverCiphers);
            var fight = new ShaperFight(BossId, boss, conn);

            // The hand-set clock: Step's own `now` is what the rotation reads, so one step well past 0 is enough for
            // the melee branch (the variety's AttackRange is 12 and the player stands 5 cells away).
            await fight.Step(conn, 5.0);
            Assert.True(boss.ActionGeneration > 0, "the controller ordered no action at all");

            // The wire carries it: the 0x1ab the client needs, decrypted back to the frame the client would read.
            var header = await ReadFrame(client.GetStream(), clientCiphers, 2);
            Assert.Equal(0x01ab, (ushort)(header[0] << 8 | header[1]));
            // MonsterSwing's own order (Monsters.MonsterSwingOrderLocked) is what stamps these.
            Assert.True(boss.SwingGeneration > 0, "the swing never went through the send gate");
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    [Fact]
    public async Task Entry_protection_leaves_the_ordered_swing_off_the_wire()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accepting;

        var serverCiphers = new ConnectionCiphers(new byte[64], isClient: false);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());

            var (conn, boss) = MeleeSetup(server.GetStream(), serverCiphers);
            // Exactly what PlayerEnteredArea starts: Monsters.MonsterSwing returns before it orders anything while this
            // is up, so the live windows (every one of them inside a fresh ~60 s window) saw no swing at all.
            conn.EntryProtection.Start(0, (100, 100), GracePeriod.BuffRow, town: false, hideout: false);
            Assert.True(Game.InGracePeriod(conn));
            var fight = new ShaperFight(BossId, boss, conn);

            // The controller still drives: the same step that put 0x1ab on the wire above orders its swing here too
            // (the fight's own trace prints "swing 481 returned after 0 ms"). Only the wire is silent.
            await fight.Step(conn, 5.0);
            Assert.True(boss.ActionGeneration > 0, "the controller ordered no action at all");

            // Bounded wait for the fire-and-forget MonsterSwing: the same swing crossed the wire in the test above.
            var header = new byte[2];
            using var arrived = new EventWaitHandle(false, EventResetMode.ManualReset);
            _ = Task.Run(() =>
            {
                try { if (client.GetStream().Read(header, 0, header.Length) > 0) arrived.Set(); }
                catch (Exception) { }
            });
            Assert.False(arrived.WaitOne(TimeSpan.FromSeconds(1)),
                "a swing reached the client despite the player's entry protection");
            // The swing never reached the send gate, which is what stamps this.
            Assert.Null(boss.SwingGeneration);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    // A connection whose player stands five cells from a spawned Shaper, in a real socket: the boss monster
    // SpawnMonster builds for /spawn_mob Metadata/Monsters/AtlasBosses/TheShaperBoss@83 unique (the variety's row, its
    // life, its three life thresholds), placed in the connection's own monster table under the id the live run used.
    static (ClientState Conn, SpawnedMonster Boss) MeleeSetup(NetworkStream toClient, ConnectionCiphers serverCiphers)
    {
        var conn = new ClientState(new(1, "boss-drive", 100, 100, 100, 100, null, []), 1, 1)
        {
            PlayerLife = 1000, PlayerSent = true, Character = "BossDrive",
        };
        conn.Wire = ("127.0.0.1:1", toClient, serverCiphers);
        conn.Walk.Teleport(100, 100, 0);
        var variety = new MonsterCatalog().Find(Shaper);
        Assert.NotNull(variety);
        Assert.True(AtlasBosses.IsBoss(variety));
        var boss = new SpawnedMonster("The Shaper@83", new PlayerWalk(100, 95, 20, null, size: 2), 60, 6, maxLife: 3129484)
        {
            Variety = variety, Level = 83, Life = 3129484, MaxLife = 3129484, LifePhases = new([75, 50, 25]),
        };
        conn.Monsters[BossId] = boss;
        return (conn, boss);
    }

    static async Task<byte[]> ReadFrame(NetworkStream stream, ConnectionCiphers ciphers, int length)
    {
        var bytes = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(bytes.AsMemory(read)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        ciphers.Receive.Process(bytes);
        return bytes;
    }
}
