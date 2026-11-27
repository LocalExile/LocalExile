using System.Net;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class LocalGateCheckpointTests
{
    static uint LabelHash(string key)
    {
        var row = ClientTables.Table("ClientStrings").First(r => r.Text("Id") == key);
        return (uint)row.Long("HASH32");
    }

    [Fact]
    public async Task Grand_arena_ring_of_blades_resurrect_at_checkpoint_revives_outside_boss_room_not_at_zone_entrance()
    {
        ushort grandArenaHash = AreaTransitionCatalog.HashOf("1_4_5_2") ?? 0x4520;
        var zoneEntrance = (X: 25, Y: 25);
        var waypointCell = (X: 35, Y: 35);
        var arena1Outside = (X: 110, Y: 90);
        var arena1Inside = (X: 150, Y: 90);
        var hall2Arrival = (X: 195, Y: 120);
        var ringOutside = (X: 320, Y: 410);
        var ringDoor = (X: 324, Y: 412);
        var ringInside = (X: 460, Y: 460);

        // Keep an unwalkable pocket around (319, 409) to verify walkable fallback stays on the exterior side.
        bool Walkable(int x, int y) => x is >= 0 and < 600 && y is >= 0 and < 600 && !(x == 319 && y == 409);

        var enterArena1 = new AreaTransitionObject(112, 92, grandArenaHash, null)
            { TeleportTo = arena1Inside, Kind = 1, Label = LabelHash("GrandArenaExterior") };
        var exitArena1ToHall2 = new AreaTransitionObject(160, 95, grandArenaHash, null)
            { TeleportTo = hall2Arrival, Kind = 1, Label = LabelHash("GrandArenaInterior") };
        var enterRingOfBlades = new AreaTransitionObject(ringDoor.X, ringDoor.Y, grandArenaHash, null)
            { TeleportTo = ringInside, Kind = 1, Label = LabelHash("DaressoArenaName") };
        var exitRingOfBlades = new AreaTransitionObject(ringInside.X + 4, ringInside.Y + 4, grandArenaHash, null)
            { TeleportTo = ringDoor, Kind = 1, Label = LabelHash("GrandArenaInterior") };

        var layout = new AreaLayout(grandArenaHash, "1_4_5_2", 600, 600, zoneEntrance.X, zoneEntrance.Y, Walkable,
            [(0x2000u, enterArena1), (0x2001u, exitArena1ToHall2), (0x2002u, enterRingOfBlades), (0x2003u, exitRingOfBlades)])
        {
            Waypoint = new WaypointObject(waypointCell.X, waypointCell.Y, Active: 1),
        };

        var instance = new InstanceState();
        var peer = new Player(layout, 2, 1) { Instance = instance, Character = { Name = "GrandArenaPeer" } };
        var player = new Player(layout, 1, 1) { Instance = instance, Character = { Name = "GrandArenaRunner" } };
        player.Character.Waypoints.Add("1_4_5_2");

        // Before crossing any local gate, checkpoint is the activated waypoint (not zone start).
        var beforeGates = Game.ResolveCheckpoint(player, inTown: false);
        Assert.Equal("the activated waypoint", beforeGates.Reason);

        // 1. Entering Arena 1 sets checkpoint just outside Arena 1, leaving peer untouched.
        Game.RememberLocalGateCheckpoint(player, enterArena1, arena1Outside, arena1Inside);
        Assert.Equal(arena1Outside, Game.LocalGateCheckpointOf(player));
        Assert.Null(Game.LocalGateCheckpointOf(peer));

        // 2. Exiting Arena 1 forward into Passageway 2 moves checkpoint to Passageway 2 (never inside Arena 1).
        Game.RememberLocalGateCheckpoint(player, exitArena1ToHall2, arena1Inside, hall2Arrival);
        Assert.Equal(hall2Arrival, Game.LocalGateCheckpointOf(player));

        // 3. Entering The Ring of Blades (Daresso boss room) sets checkpoint to the walkable spot just outside
        //    its entrance door — overriding the earlier activated waypoint and never inside Daresso's room.
        Game.RememberLocalGateCheckpoint(player, enterRingOfBlades, (319, 409), ringInside);
        var fallbackExterior = Game.LocalGateCheckpointOf(player);
        Assert.NotNull(fallbackExterior);
        Assert.True(layout.WalkableFor(fallbackExterior.Value.X, fallbackExterior.Value.Y, 1));
        Assert.True(Math.Abs(fallbackExterior.Value.X - 319) <= 2 && Math.Abs(fallbackExterior.Value.Y - 409) <= 2);

        Game.RememberLocalGateCheckpoint(player, enterRingOfBlades, ringOutside, ringInside);
        Assert.Equal(ringOutside, Game.LocalGateCheckpointOf(player));
        var resolved = Game.ResolveCheckpoint(player, inTown: false);
        Assert.Equal((ringOutside.X, ringOutside.Y, "the last local gate"), resolved);

        // 4. End-to-end Resurrect at Checkpoint sends 0x00cb FadeMove to the Ring of Blades exterior entrance
        //    (neither the zone entrance nor inside Daresso's boss room).
        using var wire = new ResurrectWire(layout, instance, "GrandArenaRunner");
        wire.Player.Character.Waypoints.Add("1_4_5_2");
        wire.Player.PlayerLife = 0;
        var resurrectTask = Game.Resurrect(wire.Player, inTown: false);
        var (fadeX, fadeY) = await wire.ReadFadeMoveTargetAsync();
        wire.Player.Closed = true;
        await resurrectTask;
        Assert.Equal(ringOutside, (fadeX, fadeY));
        Assert.NotEqual(zoneEntrance, (fadeX, fadeY));
        Assert.NotEqual(ringInside, (fadeX, fadeY));

        // 5. Exiting back out of The Ring of Blades keeps the checkpoint outside (never inside Daresso's room).
        Game.RememberLocalGateCheckpoint(player, exitRingOfBlades, ringInside, ringOutside);
        Assert.Equal(ringOutside, Game.LocalGateCheckpointOf(player));

        // 6. Re-entering the same instance via Town Portal (new connection, same character + instance) preserves
        //    the checkpoint; non-portal re-entry clears it; a brand-new instance starts with no checkpoint.
        var portalReturner = new Player(layout, 3, 1) { Instance = instance, Character = { Name = "GrandArenaRunner" }, ArriveAt = ringInside };
        Assert.Equal(ringOutside, Game.LocalGateCheckpointOf(portalReturner));
        Game.ClearLocalGateCheckpoint(portalReturner);
        Assert.Null(Game.LocalGateCheckpointOf(portalReturner));
        Game.RememberLocalGateCheckpoint(player, enterRingOfBlades, ringOutside, ringInside);
        Assert.Null(Game.LocalGateCheckpointOf(new Player(layout, 4, 1) { Instance = new(), Character = { Name = "GrandArenaRunner" } }));
    }

    [Fact]
    public void Intra_area_checkpoints_handle_campaign_boss_arenas_returns_and_map_exits()
    {
        static Player MakePlayer(string areaId, ushort hash, string name)
        {
            var layout = new AreaLayout(hash, areaId, 600, 600, 20, 20, (_, _) => true, []);
            return new Player(layout, 1, 1) { Instance = new(), Character = { Name = name } };
        }

        // Act 7 Temple of Decay L2 (Arakaali): BossArenaEntranceTransition puts checkpoint outside the boss door.
        var arakaali = MakePlayer("2_7_12_2", 0x7122, "ArakaaliTester");
        var arakaaliIn = new AreaTransitionObject(100, 100, arakaali.Layout.Hash, null)
            { TeleportTo = (350, 350), Kind = 1, SourceType = "Metadata/QuestObjects/BossArenaEntranceTransition" };
        var arakaaliOut = new AreaTransitionObject(350, 350, arakaali.Layout.Hash, null)
            { TeleportTo = (100, 100), Kind = 1 };
        Game.RememberLocalGateCheckpoint(arakaali, arakaaliIn, (96, 98), (348, 348));
        Assert.Equal((96, 98), Game.LocalGateCheckpointOf(arakaali));
        Game.RememberLocalGateCheckpoint(arakaali, arakaaliOut, (348, 348), (104, 102));
        Assert.Equal((104, 102), Game.LocalGateCheckpointOf(arakaali));

        // Act 3 Lunaris Temple L2 (Piety): Portal1 enters boss room (departure), Portal2 returns outside (arrival).
        var piety = MakePlayer("1_3_14_2", 0x3142, "PietyTester");
        var pietyIn = new AreaTransitionObject(80, 80, piety.Layout.Hash, null)
            { TeleportTo = (300, 300), Kind = 1, Label = LabelHash("Portal"), SourceType = "Metadata/QuestObjects/Piety/PietyArenaPortal1" };
        var pietyOut = new AreaTransitionObject(300, 300, piety.Layout.Hash, null)
            { TeleportTo = (80, 80), Kind = 1, Label = LabelHash("Portal"), SourceType = "Metadata/QuestObjects/Piety/PietyArenaPortal2" };
        Game.RememberLocalGateCheckpoint(piety, pietyIn, (78, 82), (298, 298));
        Assert.Equal((78, 82), Game.LocalGateCheckpointOf(piety));
        Game.RememberLocalGateCheckpoint(piety, pietyOut, (298, 298), (84, 82));
        Assert.Equal((84, 82), Game.LocalGateCheckpointOf(piety));

        // Act 6 Prisoner's Gate (Abberath): P1 entrance -> safe Cloven Pass connector -> P2 entrance -> return out.
        var abberath = MakePlayer("2_6_8", 0x6008, "AbberathTester");
        var abP1 = new AreaTransitionObject(50, 50, abberath.Layout.Hash, null)
            { TeleportTo = (180, 180), Kind = 1, Label = LabelHash("ArenaNameAbberath1") };
        var abPass = new AreaTransitionObject(200, 200, abberath.Layout.Hash, null)
            { TeleportTo = (280, 280), Kind = 1, Label = LabelHash("ArenaNameAbberath2") };
        var abP2 = new AreaTransitionObject(310, 310, abberath.Layout.Hash, null)
            { TeleportTo = (450, 450), Kind = 1, Label = LabelHash("ArenaNameAbberath3") };
        var abOut = new AreaTransitionObject(460, 460, abberath.Layout.Hash, null)
            { TeleportTo = (50, 50), Kind = 1 };
        Game.RememberLocalGateCheckpoint(abberath, abP1, (48, 52), (178, 178));
        Assert.Equal((48, 52), Game.LocalGateCheckpointOf(abberath));
        Game.RememberLocalGateCheckpoint(abberath, abPass, (198, 198), (282, 282));
        Assert.Equal((282, 282), Game.LocalGateCheckpointOf(abberath));
        Game.RememberLocalGateCheckpoint(abberath, abP2, (308, 312), (448, 448));
        Assert.Equal((308, 312), Game.LocalGateCheckpointOf(abberath));
        Game.RememberLocalGateCheckpoint(abberath, abOut, (458, 458), (54, 52));
        Assert.Equal((54, 52), Game.LocalGateCheckpointOf(abberath));

        // Act 9 Rotting Core (Depraved Trinity): Black Core hub -> gauntlet -> organ boss door -> return -> Black Heart.
        var core = MakePlayer(DepravedTrinity.Area, 0x9102, "RottingCoreTester");
        var toHub = new AreaTransitionObject(40, 40, core.Layout.Hash, null)
            { TeleportTo = (150, 150), Kind = 1, Label = LabelHash("MalachaiArenaName1") };
        var toOrganBoss = new AreaTransitionObject(240, 240, core.Layout.Hash, null)
            { TeleportTo = (340, 340), Kind = 1, Label = LabelHash("DoedreArenaName"), SourceType = "Metadata/Terrain/Mountain/Belly/Objects/BellyArenaTransition" };
        var organReturn = new AreaTransitionObject(350, 350, core.Layout.Hash, null)
            { TeleportTo = (150, 150), Kind = 1, Label = LabelHash("MalachaiArenaName1"), SourceType = "Metadata/MiscellaneousObjects/PortalToggleable" };
        var toBlackHeart = new AreaTransitionObject(160, 160, core.Layout.Hash, null)
            { Kind = 1, Label = LabelHash("MalachaiArenaName2"), SourceType = "Metadata/QuestObjects/Act9/HarvestFinalBossTransition" };
        Game.RememberLocalGateCheckpoint(core, toHub, (38, 40), (152, 152));
        Assert.Equal((152, 152), Game.LocalGateCheckpointOf(core));
        Game.RememberLocalGateCheckpoint(core, toOrganBoss, (238, 242), (338, 338), allowUnpaired: true);
        Assert.Equal((238, 242), Game.LocalGateCheckpointOf(core));
        Game.RememberLocalGateCheckpoint(core, organReturn, (348, 348), (154, 154), allowUnpaired: true);
        Assert.Equal((154, 154), Game.LocalGateCheckpointOf(core));
        Game.RememberLocalGateCheckpoint(core, toBlackHeart, (158, 162), (480, 480), allowUnpaired: true);
        Assert.Equal((158, 162), Game.LocalGateCheckpointOf(core));

        // Act 4 Harvest: entering Black Core (MalachaiArenaName1) is a boss room; MalachaiDepthsReturn exits to exterior.
        var harvest = MakePlayer("1_4_6_3", 0x4630, "HarvestTester");
        var harvestCoreIn = new AreaTransitionObject(70, 70, harvest.Layout.Hash, null)
            { TeleportTo = (250, 250), Kind = 1, Label = LabelHash("MalachaiArenaName1") };
        var harvestDepthsReturn = new AreaTransitionObject(400, 400, harvest.Layout.Hash, null)
            { TeleportTo = (255, 255), Kind = 1, Label = LabelHash("MalachaiArenaName1"), SourceType = "Metadata/QuestObjects/Act4/MalachaiDepthsReturn" };
        Game.RememberLocalGateCheckpoint(harvest, harvestCoreIn, (68, 72), (250, 250));
        Assert.Equal((68, 72), Game.LocalGateCheckpointOf(harvest));
        Game.RememberLocalGateCheckpoint(harvest, harvestDepthsReturn, (398, 398), (255, 255));
        Assert.Equal((255, 255), Game.LocalGateCheckpointOf(harvest));

        // Unlabelled local gate with an active Unique boss at the destination keeps checkpoint outside.
        var generic = MakePlayer("1_4_4_3", 0x4430, "BossFallbackTester");
        var daresso = Game.Monsters.Find("Metadata/Monsters/Daresso/DaressoBoss");
        generic.Area.Monsters[0x9001u] = new SpawnedMonster("Daresso",
            new PlayerWalk(420, 420, 20f, (_, _) => true, 600, 600), 60, 12, 1000)
        {
            ObjectId = 0x9001u,
            Level = 38,
            Rarity = MonsterRarity.Unique,
            Hostile = true,
            Variety = daresso,
        };
        var unlabelledBossDoor = new AreaTransitionObject(120, 120, generic.Layout.Hash, null) { TeleportTo = (400, 400), Kind = 1 };
        Game.RememberLocalGateCheckpoint(generic, unlabelledBossDoor, (118, 122), (400, 400));
        Assert.Equal((118, 122), Game.LocalGateCheckpointOf(generic));

        // Active dynamic door barrier covering the saved cell steps clear onto an adjacent walkable cell.
        generic.DoorBarriers[1] = new HashSet<(int X, int Y)> { (118, 122) };
        generic.UpdateBarrier();
        var cleared = Game.LocalGateCheckpointOf(generic);
        Assert.NotNull(cleared);
        Assert.NotEqual((118, 122), cleared!.Value);
        generic.DoorBarriers.Clear();
        generic.UpdateBarrier();

        // Towns and cross-area exits do not record local gate checkpoints; maps still exit to their device origin.
        var town = MakePlayer("1_1_town", AreaLayouts.LioneyesWatch, "TownTester");
        Game.RememberLocalGateCheckpoint(town, new AreaTransitionObject(50, 50, town.Layout.Hash, null) { TeleportTo = (80, 80) }, (48, 50), (80, 80));
        Assert.Null(Game.LocalGateCheckpointOf(town));

        Game.RememberLocalGateCheckpoint(arakaali, arakaaliIn with { DestinationHash16 = 0x1234 }, (10, 10), (20, 20));
        Assert.Equal((104, 102), Game.LocalGateCheckpointOf(arakaali));

        const string mapOwner = "checkpoint-map-owner";
        var mapArea = new MapDevice.MapArea(0x2a5a, "MapWorldsStrand", "Strand", 1);
        var origin = new MapDevice.Origin(0x1234, 9, 44, 55);
        try
        {
            var run = MapDevice.Activate(mapOwner, mapArea, entrance: origin);
            MapDevice.Entered(run, 987910);
            var mapLayout = new AreaLayout(mapArea.Hash16, mapArea.Id, 600, 600, 20, 20, (_, _) => true, []);
            var inMap = new Player(mapLayout, 1, 1) { Instance = new(), InstanceNumber = 987910, Character = { Name = mapOwner } };
            var mapBossDoor = new AreaTransitionObject(100, 100, mapArea.Hash16, null) { TeleportTo = (300, 300), Kind = 1, Label = LabelHash("Arena") };
            Game.RememberLocalGateCheckpoint(inMap, mapBossDoor, (98, 102), (300, 300));
            var exit = Game.MapCheckpointExit(inMap);
            Assert.NotNull(exit);
            Assert.Equal((origin.Area, origin.Instance, (int?)origin.X, (int?)origin.Y),
                (exit!.Area, exit.Instance, exit.ArriveAt?.X, exit.ArriveAt?.Y));
        }
        finally { MapDevice.Close(mapOwner); }
    }

    sealed class ResurrectWire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public readonly Player Player;

        public ResurrectWire(AreaLayout layout, InstanceState instance, string character)
        {
            Player = new(layout, 1, 1)
            {
                Instance = instance,
                PlayerLife = 100,
            };
            Player.Character.Name = character;
            Player.Character.ClassRow = 1;
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            Player.Connection.Session = new(character, writer.GetStream(), Ciphers);
        }

        public async Task<(int X, int Y)> ReadFadeMoveTargetAsync()
        {
            var stream = reader.GetStream();
            var buf = new byte[4096];
            var raw = new List<byte>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                int n = await stream.ReadAsync(buf, cts.Token);
                Assert.True(n > 0, "Resurrect wire closed before 0x00cb FadeMove arrived");
                var chunk = buf.AsSpan(0, n).ToArray();
                decrypt.Receive.Process(chunk);
                raw.AddRange(chunk);
                var data = raw.ToArray();
                for (int i = 0; i + 14 <= data.Length; i++)
                {
                    // FadeMove: u16 opcode (0x00cb) + u32 targetId + i32 x + i32 y (big-endian)
                    if (data[i] == 0x00 && data[i + 1] == 0xCB)
                    {
                        uint id = (uint)((data[i + 2] << 24) | (data[i + 3] << 16) | (data[i + 4] << 8) | data[i + 5]);
                        if (id == Player.Id)
                        {
                            int x = (data[i + 6] << 24) | (data[i + 7] << 16) | (data[i + 8] << 8) | data[i + 9];
                            int y = (data[i + 10] << 24) | (data[i + 11] << 16) | (data[i + 12] << 8) | data[i + 13];
                            return (x, y);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            Player.Closed = true;
            reader.Dispose();
            writer.Dispose();
            listener.Stop();
        }
    }
}
