using System.Buffers.Binary;
using GameServer.Core.Accounts;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.HeistCache;
using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The Smuggler's Cache encounter (Modules/Core/Content/HeistCache). The create is a measured 39-byte body; the open
/// is the two component updates transplanted from another Heist chest's open (no cache's own open is known) and drops
/// nothing. What a player who joins later is given is read from the area's own object records (<c>Objects.Join</c>),
/// which is what the world sends record into.
/// </summary>
public class HeistCacheTests
{
    /// <summary>
    /// Placing a cache creates one of the client's own type, closed, as an object of the area: a player who joins
    /// later is given the create with the wake bytes at its end, and it is the measured body byte for byte (a level 70
    /// cache at cell (942, 806)). An opened cache differs only at byte 19. A player who enters afterwards, also with
    /// the module reloaded (a new encounter reads the instance's saved text), is given nothing twice.
    /// </summary>
    [Fact]
    public async Task Placing_a_cache_creates_the_measured_create_and_a_late_joiner_is_given_it()
    {
        using var world = new Fixture();
        var encounter = new HeistCacheEncounter();
        var state = encounter.For(world.First);
        await encounter.PlaceBoxes(world.First, 70, [], 1, new Random(1234), state);
        Assert.Single(state.Boxes);
        var box = state.Boxes[0];
        Assert.Equal("closed", box.Stage);
        Assert.True(world.First.Area.Objects.Has(box.Id));

        var records = world.Join(world.Second);
        var create = records.Single(r => r.Entity == box.Id && r.Kind == AreaDelta.Kind.Create).Bytes;
        Assert.Equal(InstanceEntities.CreateObjectId, BinaryPrimitives.ReadUInt16BigEndian(create));
        Assert.Equal(HeistCacheTables.TypeHash, BinaryPrimitives.ReadUInt32BigEndian(create.AsSpan(12)));
        byte[] wake = InstanceProtocol.EnterWakeZone(box.Id);
        Assert.True(create.AsSpan(create.Length - wake.Length).SequenceEqual(wake));
        Assert.True(create.AsSpan(18, 39).SequenceEqual(HeistCacheEncounter.CreateBody(box.Cell, box.Level, false)));

        // The measured create: a level 70 cache at (942, 806).
        Assert.Equal("0000ae030000260300000000000000000100000046000000000000000040400108000000010000",
            Convert.ToHexString(HeistCacheEncounter.CreateBody((942, 806), 70, false)).ToLowerInvariant());
        byte[] opened = HeistCacheEncounter.CreateBody((942, 806), 70, true);
        Assert.Equal(0x01, opened[19]);
        Assert.Equal(0x00, HeistCacheEncounter.CreateBody((942, 806), 70, false)[19]);

        // Entering again, after a reload too, creates nothing again and keeps the same cache.
        await encounter.OnPlayerEntered(world.Second);
        var reloaded = new HeistCacheEncounter();
        await reloaded.OnPlayerEntered(world.Second);
        Assert.Equal(box.Id, reloaded.For(world.Second).Boxes.Single().Id);
        Assert.Empty(world.Delta());
    }

    /// <summary>
    /// Opening a cache sends only the two component updates (Chest, empty; ObjectMagicProperties, 08 00 00 00): no
    /// create, no item, and a second open does nothing. A player who joins afterwards, or after a reload, finds it
    /// open. A click while dead is the cache's (swallowed), a click on another object is not, and an area that is not
    /// a map places none.
    /// </summary>
    [Fact]
    public async Task Opening_a_cache_sends_only_two_updates_and_a_reentry_finds_it_open()
    {
        using var world = new Fixture();
        var encounter = new HeistCacheEncounter();
        var state = encounter.For(world.First);
        await encounter.PlaceBoxes(world.First, 70, [], 1, new Random(1234), state);
        var box = state.Boxes[0];
        Assert.True(await encounter.Open(world.First, box));
        Assert.False(await encounter.Open(world.First, box));
        Assert.Equal("open", box.Stage);

        var updates = world.Join(world.Second).Where(r => r.Entity == box.Id && r.Kind == AreaDelta.Kind.World).Select(r => r.Bytes).ToList();
        Assert.Equal(2, updates.Count);
        Assert.All(updates, m => Assert.Equal(InstanceEntities.ComponentUpdateId, BinaryPrimitives.ReadUInt16BigEndian(m)));
        var chest = updates.Single(m => m[14] == HeistCacheTables.ChestComponent);
        var magic = updates.Single(m => m[14] == HeistCacheTables.MagicPropertiesComponent);
        Assert.Equal(15, chest.Length);
        Assert.Equal(19, magic.Length);
        Assert.Equal(new byte[] { 0x08, 0x00, 0x00, 0x00 }, magic.AsSpan(magic.Length - 4).ToArray());

        // After a reload the saved text brings the cache back open, on the same object.
        var reloaded = new HeistCacheEncounter();
        await reloaded.OnPlayerEntered(world.Second);
        Assert.Equal("open", reloaded.For(world.Second).Boxes.Single().Stage);
        Assert.Empty(world.Delta());

        world.First.PlayerLife = 0;
        Assert.True(await reloaded.OnUse(world.First, new InstanceTransfer.UseSkillOnObject(box.Id, 1, 1, 0), 0));
        Assert.False(await reloaded.OnUse(world.First, new InstanceTransfer.UseSkillOnObject(box.Id + 1, 1, 1, 0), 0));

        // Not a map area: the population places none on its own.
        var fresh = new HeistCacheEncounter();
        await fresh.OnAreaPopulated(new AreaPopulated(world.Other, 70, 1234, []));
        Assert.Empty(fresh.For(world.Other).Boxes);
    }

    /// <summary>Two players of one instance on a flat 600 x 600 area, each with a connection that is read and dropped.</summary>
    sealed class Fixture : IDisposable
    {
        readonly GameLogicOrNull previous = new();
        readonly ModuleLifetime lifetime = new("Game");
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly List<TcpClient> clients = [];
        readonly List<Task> drains = [];
        public readonly Player First, Second, Other;

        public Fixture()
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            listener.Start();
            var layout = new AreaLayout(0x1234, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
            var instance = new InstanceState();
            First = Make(layout, instance);
            Second = Make(layout, instance);
            Other = Make(new AreaLayout(0x1235, "1_1_town", 600, 600, 300, 300, (_, _) => true, []), new());
            // The second player is in the first one's area, as a player of the same instance is.
            var area = First.Area;
            Assert.Same(area, Second.Area);
        }

        Player Make(AreaLayout layout, InstanceState instance)
        {
            var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = accepting.Result;
            clients.Add(writer);
            clients.Add(reader);
            drains.Add(Task.Run(async () => { try { await reader.GetStream().CopyToAsync(Stream.Null); } catch (IOException) { } catch (ObjectDisposedException) { } }));
            return new Player(layout, 1, 3316637604u)
            {
                Instance = instance,
                Connection =
                {
                    LockstepClient = false,
                    PlayerSent = true,
                    Session = new("heist-cache", writer.GetStream(), new ConnectionCiphers(new byte[64], isClient: false)),
                },
                Character = { Name = "HeistCacheTest", Account = "HeistCacheTest", League = "Standard" },
                LeagueModule = new(new GameServer.Core.Accounts.League("Standard", "Standard", false, false), new HashSet<string>(["Game"])),
            };
        }

        /// <summary>What the area gives a player who joins now: its objects' latest records, for that player.</summary>
        public List<AreaDelta.Record> Join(Player joiner)
        {
            var owner = GameArea.Own(First.Area);
            try
            {
                First.Area.Objects.Join(joiner);
                return Delta();
            }
            finally { GameArea.Own(owner); }
        }

        public List<AreaDelta.Record> Delta()
        {
            List<AreaDelta.Record> all = [];
            for (int frame = 0; frame < 6; frame++) all.AddRange(First.Area.Delta.Take());
            return all;
        }

        public void Dispose()
        {
            foreach (var client in clients) client.Dispose();
            Task.WaitAll([.. drains]);
            listener.Stop();
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime!;
            previous.Restore();
        }
    }

    sealed class GameLogicOrNull
    {
        readonly IGameLogic? logic = Program.GameLogicOrNull;
        public void Restore() => Program.SetGameLogic(logic!);
    }
}
