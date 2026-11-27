using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class YugulHazardsTests
{
    [GameFilesFact]
    public async Task Yugul_orbs_and_turrets_skip_grace_protected_players_and_retain_ready_volleys()
    {
        using var runtime = new Runtime();
        using var nearWire = new Wire();
        using var farWire = new Wire();

        var placements = new QuestObjectTypes.AreaPlacements(Yugul.Area, 1, [])
            .Of("Metadata/Monsters/Frog/FrogGod/SilverPoolTurret");
        Assert.NotEmpty(placements);
        var spawn = (X: placements[0].X, Y: placements[0].Y);
        var nearPos = (X: spawn.X + 4, Y: spawn.Y);
        var farPos = (X: spawn.X - 24, Y: spawn.Y);

        var instance = new InstanceState();
        var layout = new AreaLayout(1, Yugul.Area, 4000, 4000, spawn.X, spawn.Y, (_, _) => true, []);
        Player MakePlayer(string name, Wire wire, (int X, int Y) pos)
        {
            var p = new Player(layout, 1, 1)
            {
                Character = { Name = name },
                Instance = instance,
                Connection = { LockstepClient = false, PlayerSent = true, Session = new(name, wire.Stream, wire.Ciphers) },
                PlayerLife = 10000,
                Pools = new(60, 10000, 200, 1, 0),
            };
            p.Walk.Teleport(pos.X, pos.Y, 0);
            double now = Game.AreaTime(p);
            p.EntryProtection.Start(now, pos, GracePeriod.BuffRow, town: false, hideout: false);
            return p;
        }

        var nearProtected = MakePlayer("YugulNearProtected", nearWire, nearPos);
        var farEligible = MakePlayer("YugulFarEligible", farWire, farPos);
        runtime.Players = [nearProtected, farEligible];
        Assert.True(Game.InGracePeriod(nearProtected));
        Assert.True(Game.InGracePeriod(farEligible));

        var bossVariety = Assert.IsType<MonsterCatalog.Variety>(Game.Monsters.Find(Yugul.Boss));
        uint bossId = await Game.SpawnMonster(nearProtected, bossVariety, 58, MonsterRarity.Unique, spawn.X, spawn.Y, unrisen: false);
        var boss = nearProtected.Area.Monsters[bossId];
        boss.Aggro = true;
        boss.Life = boss.MaxLife - 1; // Ensure both a turret and a Fearful orb spawn immediately.

        var sent = new List<byte[]>();
        var previousOutgoing = Program.Outgoing;
        var hazards = new YugulHazards(bossId, boss, nearProtected);
        try
        {
            Program.Outgoing = (player, bytes) =>
            {
                if (ReferenceEquals(player, nearProtected)) sent.Add(bytes.ToArray());
            };
            await hazards.Bind(nearProtected);
            await hazards.Bind(farEligible);

            // t = 0: initialize placements, spawn turret (ready at 0.8) and Fearful orb (ready at 1.3).
            await hazards.Step(nearProtected, 0);
            uint turretId = Assert.Single(nearProtected.Area.Monsters, kv => kv.Value.Variety?.Id == "Metadata/Monsters/Frog/FrogGod/SilverPoolTurret").Key;
            uint orbId = Assert.Single(nearProtected.Area.Monsters, kv => kv.Value.Variety?.Id == "Metadata/Monsters/Frog/FrogGod/SilverOrb").Key;
            var orb = nearProtected.Area.Monsters[orbId];
            var orbStart = orb.Walk.Position(0);

            sent.Clear();

            // t = 1.0 and 1.5: both players are protected — turret holds its volley and orb waits at its spawn cell.
            await hazards.Step(nearProtected, 1.0);
            await hazards.Step(nearProtected, 1.5);
            Assert.DoesNotContain(sent, p => IsAnimation(p, turretId, clip: 0));
            Assert.DoesNotContain(sent, p => IsMoveTo(p, orbId));
            Assert.Equal(orbStart, orb.Walk.Position(1.5));

            // End grace period on the farther player only; the closer player stays protected.
            farEligible.EntryProtection.End(Game.AreaTime(farEligible), farPos, action: true);
            Assert.True(Game.InGracePeriod(nearProtected));
            Assert.False(Game.InGracePeriod(farEligible));

            sent.Clear();

            // t = 1.75: ready turret immediately channels its retained volley and orb chases the farther unprotected player.
            await hazards.Step(nearProtected, 1.75);
            Assert.Contains(sent, p => IsAnimation(p, turretId, clip: 0));
            var orbMove = Assert.Single(sent, p => IsMoveTo(p, orbId));
            int moveX = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(orbMove.AsSpan(6, 4));
            Assert.True(moveX < orbStart.X, $"Expected orb to chase unprotected player at X={farPos.X} (< {orbStart.X}), got moveX={moveX}");

            // Advance the projectile runtime past the first turret release (~1.75 + 0.23 s) and verify aim direction.
            var session = Game.ProjectilesFor(nearProtected);
            session.Runtime.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(2.1));
            var shots = session.Runtime.Projectiles.Where(p => p.Use.Payload is HostileProjectileOffense o && o.MonsterId == turretId).ToArray();
            Assert.NotEmpty(shots);
            Assert.All(shots, shot =>
                Assert.True(shot.Direction.X < 0, $"Expected turret shot to aim toward unprotected player at X={farPos.X}, got Direction.X={shot.Direction.X}"));
        }
        finally
        {
            hazards.Stop();
            Program.Outgoing = previousOutgoing;
        }
    }

    static bool IsAnimation(byte[] packet, uint entityId, int clip) =>
        packet.Length >= 17
        && (packet[0] << 8 | packet[1]) == InstanceProtocol.AnimationUpdateId
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2, 4)) == entityId
        && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(13, 4)) == clip;

    static bool IsMoveTo(byte[] packet, uint entityId) =>
        packet.Length >= 14
        && (packet[0] << 8 | packet[1]) == InstanceProtocol.MoveToId
        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2, 4)) == entityId;

    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-yugul-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public Player[] Players = [];
        public Runtime() { Saves.Open(saves); Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic()); }
        public void Dispose()
        {
            foreach (var p in Players) p.Closed = true;
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
        public void Dispose() { writer.Dispose(); reader.Dispose(); listener.Dispose(); }
    }
}
