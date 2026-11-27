using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class RepeatedAreaEntryTests
{
    [Fact]
    public void Both_ship_graveyard_cave_edges_keep_their_own_arrival_in_both_directions()
    {
        ushort outside = AreaTransitionCatalog.HashOf("1_1_9")!.Value;
        ushort cave = AreaTransitionCatalog.HashOf("1_1_9a")!.Value;
        var layout = new AreaLayout(cave, "1_1_9a", 300, 300, 100, 100, null,
            [(1, new(20, 30, outside) { Entrance = 1 }), (2, new(200, 210, outside) { Entrance = 2 })]);
        foreach (int source in new[] { 2, 3 })
        {
            int destination = AreaTransitionCatalog.PairedEntrance(outside, cave, source)!.Value;
            Assert.Equal(source - 1, destination);
            Assert.Equal(source, AreaTransitionCatalog.PairedEntrance(cave, outside, destination));
            Assert.Equal(source == 2 ? (28, 30) : (208, 210), layout.Arrival(outside, destination));
            var chooser = new ChooserList(cave, [1], ArrivalEntrance: destination);
            Assert.Equal(destination, chooser.ArrivalEntrance);
        }
        Assert.Equal((28, 30), layout.Arrival(outside));
        Assert.Null(AreaTransitionCatalog.PairedEntrance(outside, cave, 99));
        Assert.Null(AreaTransitionCatalog.PairedEntrance(outside, AreaTransitionCatalog.HashOf("1_1_11_1")!.Value, 4));
    }

    [Fact]
    public void Control_blocks_arrival_prefers_authored_entrance_slot_and_skips_unwalkable_fallback()
    {
        ushort tower = AreaTransitionCatalog.HashOf("1_5_town")!.Value;
        ushort blocks = AreaTransitionCatalog.HashOf("1_5_2")!.Value;
        // 1_5_2 has entrance1 (authored slot) and entrance2disabled (unslotted fallback) both connected to 1_5_town.
        var entrances = new GameServer.Data.Terrain.Generation.EntrancePoint[]
        {
            new(2, Enabled: false, X: 116, Y: 188, NamedPoint: false),
            new(1, Enabled: true, X: 221, Y: 393, NamedPoint: true),
        };
        Assert.Equal((221, 393, true), AreaTransitionCatalog.EntrancePoint(blocks, entrances, tower));

        // When both entrances have authored slots (Submerged Passage from Flooded Depths), the disabled return still wins.
        ushort passage = AreaTransitionCatalog.HashOf("1_1_4_1")!.Value;
        ushort depths = AreaTransitionCatalog.HashOf("1_1_4_0")!.Value;
        var passageEntrances = new GameServer.Data.Terrain.Generation.EntrancePoint[]
        {
            new(3, Enabled: true, X: 100, Y: 100, NamedPoint: true),
            new(4, Enabled: false, X: 250, Y: 250, NamedPoint: true),
        };
        Assert.Equal((250, 250, true), AreaTransitionCatalog.EntrancePoint(passage, passageEntrances, depths));

        // An unslotted fallback at an unwalkable pocket never overrides the walkable transition back or strands the player.
        var layout = new AreaLayout(blocks, "1_5_2", 500, 500, 219, 394, (x, y) => x >= 200 && y >= 350,
            [(1, new(218, 363, tower) { Entrance = 1 })])
        {
            EntranceFrom = (_, _) => (116, 188, false),
        };
        var arrival = layout.Arrival(tower);
        Assert.True(layout.Walkable!(arrival.X, arrival.Y));
        Assert.True(Math.Max(Math.Abs(arrival.X - 218), Math.Abs(arrival.Y - 363)) <= 20);
    }

    [Fact]
    public async Task Town_stash_and_guild_stash_created_on_lockstep_entry_replay_on_reentry_without_duplicating_npcs()
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var w1 = new TcpClient();
        var a1 = listener.AcceptTcpClientAsync();
        await w1.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var r1 = await a1;
        var drain1 = r1.GetStream().CopyToAsync(Stream.Null);
        using var w2 = new TcpClient();
        var a2 = listener.AcceptTcpClientAsync();
        await w2.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var r2 = await a2;
        var drain2 = r2.GetStream().CopyToAsync(Stream.Null);
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            var instance = new InstanceState();
            var layout = new AreaLayout(AreaLayouts.LioneyesWatch, "1_1_town", 690, 414, 197, 261, (_, _) => true, []);
            Player Make(TcpClient writer) => new(layout, 1, 1)
            {
                Instance = instance,
                Connection =
                {
                    LockstepClient = true,
                    PlayerSent = true,
                    Session = new("reentry-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false)),
                },
                Character = { Name = "ReentryTest", Account = "ReentryTest", League = "Standard" },
                LeagueModule = new(new League("Standard", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            var first = Make(w1);
            var returning = Make(w2);

            await Game.SpawnTownStash(first);
            var tarkleigh = Game.Npcs.Find("Metadata/NPC/Act1/Tarkleigh")!;
            uint npcId = await Game.SpawnNpc(first, tarkleigh, 389, 183, 4.56f);
            // A player entity (0x20000000 range) must never be recorded in Area.Objects (players join from InstanceState).
            await Game.SendWorldCreate(first, first.Id,
                InstanceEntities.CreateObject(first.Id, InstanceEntities.ObjectTypeHash(StashObject.MetadataPath), new StashObject(197, 261).Blob()),
                "0x02bb create current player");

            Assert.NotEqual(0u, first.StashObject);
            Assert.NotEqual(0u, first.GuildStashObject);
            Assert.True(first.Area.Objects.Has(first.StashObject));
            Assert.True(first.Area.Objects.Has(first.GuildStashObject));
            Assert.False(first.Area.Objects.Has(npcId));
            Assert.False(first.Area.Objects.Has(first.Id));

            // Joining the original player must not duplicate objects already written directly to it;
            // joining a returning player replays both Stash and Guild Stash creates + wake zones.
            var previousOwner = GameArea.Own(first.Area);
            try
            {
                first.Area.Objects.Join(first);
                Assert.Empty(first.Area.Delta.Take());

                first.Area.Objects.Join(returning);
                var frame1 = first.Area.Delta.Take();
                var frame2 = first.Area.Delta.Take();
                Assert.Contains(frame1, r => r.Entity == first.StashObject && r.Kind == AreaDelta.Kind.Create
                    && ReferenceEquals(r.Only, returning) && BinaryPrimitives.ReadUInt16BigEndian(r.Bytes) == InstanceEntities.CreateObjectId);
                Assert.Contains(frame1, r => r.Entity == first.GuildStashObject && r.Kind == AreaDelta.Kind.Create
                    && ReferenceEquals(r.Only, returning) && BinaryPrimitives.ReadUInt16BigEndian(r.Bytes) == InstanceEntities.CreateObjectId);
                Assert.Contains(frame2, r => r.Entity == first.StashObject
                    && ReferenceEquals(r.Only, returning) && BinaryPrimitives.ReadUInt16BigEndian(r.Bytes) == InstanceProtocol.EnterWakeZoneId);
                Assert.Contains(frame2, r => r.Entity == first.GuildStashObject
                    && ReferenceEquals(r.Only, returning) && BinaryPrimitives.ReadUInt16BigEndian(r.Bytes) == InstanceProtocol.EnterWakeZoneId);
            }
            finally { GameArea.Own(previousOwner); }

            await Game.EnterAreaFixtures(returning);
            Assert.Equal(first.StashObject, returning.StashObject);
            Assert.Equal(first.GuildStashObject, returning.GuildStashObject);
        }
        finally
        {
            w1.Dispose();
            w2.Dispose();
            await Task.WhenAll(drain1, drain2);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }
}
