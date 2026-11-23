using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class DoedreSewersTests
{
    [Fact]
    public void Hazard_animation_emits_kind_zero_play_clip_packet()
    {
        // S->C 0x1b2 (retail 0x141fe58a0): byte 12 is u8 kind; playing a rig clip must use kind 0 (21 B),
        // never an object's Animated component index.
        byte[] start = DoedreSewers.HazardAnimation(0x3001, 1);
        byte[] loop = DoedreSewers.HazardAnimation(0x3001, 2);
        byte[] drain = DoedreSewers.HazardAnimation(0x3001, 0);

        Assert.Equal("01B2" + "00003001" + "00000000" + "0000" + "00" + "00000001" + "3F800000", Convert.ToHexString(start));
        Assert.Equal("01B2" + "00003001" + "00000000" + "0000" + "00" + "00000002" + "3F800000", Convert.ToHexString(loop));
        Assert.Equal("01B2" + "00003001" + "00000000" + "0000" + "00" + "00000000" + "3F800000", Convert.ToHexString(drain));
    }

    [GameFilesFact]
    public async Task Doedre_sewers_liquid_animations_use_kind_zero()
    {
        using var runtime = new Runtime();
        using var wire = new Wire();
        var c = new Player(new(1, "2_8_2_2", 4000, 4000, 1000, 1000, (_, _) => true, []), 1, 1)
        {
            Character = { Name = "DoedreAnimKindRegression" },
            Instance = new(),
            Connection = { LockstepClient = false, PlayerSent = true, Session = new("doedre-anim-kind", wire.Stream, wire.Ciphers) },
            PlayerLife = 10000,
            Pools = new(60, 10000, 200, 1, 0),
        };
        runtime.Player = c;
        c.Walk.Teleport(1000, 1000, 0);
        var boss = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(DoedreSewers.Boss));
        await Game.SpawnMonster(c, boss, 58, MonsterRarity.Unique, 1010, 1010);

        var sent = new List<byte[]>();
        var previousOutgoing = Program.Outgoing;
        try
        {
            Program.Outgoing = (player, bytes) =>
            {
                if (ReferenceEquals(player, c)) sent.Add(bytes.ToArray());
            };
            await DoedreSewers.Tick(c);
        }
        finally
        {
            Program.Outgoing = previousOutgoing;
            DoedreSewers.CleanupOnUnload().Dispose();
        }

        // S->C 0x1b2 (retail 0x141fe58a0) carries u8 kind at byte 12; playing a rig clip must use kind 0 (21 B),
        // never the Animated component index.
        var anims = sent.Where(p => p.Length >= 2 && (p[0] << 8 | p[1]) == InstanceProtocol.AnimationUpdateId).ToArray();
        Assert.True(anims.Length >= 2, $"expected cauldron and pipe liquid 0x1b2 updates, got {anims.Length}");
        Assert.All(anims, p =>
        {
            Assert.Equal(21, p.Length);
            Assert.Equal(0, p[12]);
        });
    }

    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-doedre-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public Player? Player;
        public Runtime() { Saves.Open(saves); Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic()); }
        public void Dispose()
        {
            if (Player is { } c) c.Closed = true;
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start();
            var accept = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accept.GetAwaiter().GetResult();
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
