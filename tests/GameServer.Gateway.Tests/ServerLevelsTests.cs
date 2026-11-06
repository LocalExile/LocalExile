using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public class ServerLevelsTests
{
    // A tier-16 Chateau (0x1d level 83) the client generated at seed 1174023763: tile hash 3728131682 in its
    // Client.txt and generation trace (2026-11-01). At its WorldAreas level (71) the topology pick differs
    // (chateau_1.dgr, not chateau_2.dgr) and so does the whole layout.
    [Fact]
    public void MapsAreGeneratedAtTheLevelTheyArePlayedAt()
    {
        const string chateau = "MapWorldsChateau";
        const uint seed = 1174023763;
        if (ServerLevels.For(chateau, seed) is not { } atAreaLevel) return; // needs the game files (data/game-files.zip)
        Assert.Equal(2796439874u, atAreaLevel.TileHash);

        Assert.Equal(3728131682u, ServerLevels.For(chateau, seed, 83)?.TileHash);
        Assert.Equal(seed, ServerLevels.PlayableSeed(chateau, seed, 83));
        // The lookups without a level (monster packs, heights, projectiles) get the level it is played at.
        Assert.Equal(3728131682u, ServerLevels.For(chateau, seed)?.TileHash);
    }

    // Two loads the 3.29 server sent (the server corpus, Agents/docs/re/011 §8): generated with the area stats of
    // their 0x1d and the server's topology variant (59), the Gateway's tile hash is the client's. The Coast at level 2 with
    // the Deepwater league flag and map_area_underlevelled; a tier-1 Chateau (level 68) with its map's whole stat list.
    // The level objects the server spawned in that Coast (every one its stream reached: the three exits, the waypoint,
    // the Karui stone, the Deepwater boat, bathysphere and sockets) are in the level's server-object list at their type
    // and sub-cell, with the server's rotation, world z and height marker (−z / 7.8125 truncated). And with
    // map_force_side_area in the stats (the VaalSideAreas mod) the generator places the Coast's Vaal side area instead
    // of rolling for it: its side cave room (node "cave1", "sidearea1") holds the transition tile that is the way in
    // (ExitKind.SideArea), which the catalog sends as a type-2 transition to one of the Coast's WorldAreas VaalArea rows.
    [Fact]
    public void ServerLoadsAreReproducedWithTheirAreaStatsAndObjects()
    {
        const string coast = "1_1_2";
        const uint coastSeed = 196634105;
        var coastStats = new Dictionary<uint, int> { [8213] = 1, [22838] = 1 };
        if (ServerLevels.For(coast, coastSeed) is null) return; // needs the game files (data/game-files.zip)
        Assert.Equal(coastSeed, ServerLevels.PlayableSeed(coast, coastSeed, 2, coastStats));
        var level = ServerLevels.For(coast, coastSeed, 2);
        Assert.NotNull(level);
        Assert.Equal(1931712098u, level.TileHash);
        (string Type, int X, int Y, float Rotation, int Step, float Z)[] spawned =
        [
            ("Metadata/MiscellaneousObjects/AreaTransition", 328, 432, 0.000f, 24, -193.770f),
            ("Metadata/NPC/Info/Karui/Stone1", 1094, 933, 0.000f, 48, -375.000f),
            ("Metadata/Terrain/Leagues/Deepwater/Objects/CoastBathysphere", 1439, 492, 3.142f, -28, 223.000f),
            ("Metadata/Terrain/Doodads/Leagues/Deepwater/BlankSocket", 1448, 492, 4.712f, 33, -259.000f),
            ("Metadata/Terrain/Doodads/Leagues/Deepwater/BlankSocket", 1430, 492, 4.712f, 33, -259.000f),
            ("Metadata/Terrain/Leagues/Deepwater/Act1_Coast/DeepwaterTutorialBoat", 1724, 552, 2.728f, -6, 50.000f),
            ("Metadata/MiscellaneousObjects/AreaTransition", 1793, 411, 4.712f, 18, -148.210f),
            ("Metadata/MiscellaneousObjects/Waypoint", 1782, 632, 4.712f, 2, -16.917f),
            ("Metadata/MiscellaneousObjects/AreaTransition", 1890, 637, 1.571f, 15, -121.343f),
        ];
        foreach (var s in spawned)
        {
            var o = Assert.Single(level.ServerObjects, p => p.Object.Type == s.Type && p.Object.X == s.X && p.Object.Y == s.Y);
            Assert.True(o.Sent, s.Type);
            Assert.Equal(s.Rotation, o.Object.Rotation % (MathF.PI * 2), 0.002);
            Assert.Equal(s.Z, o.Object.Z, 0.01);
            Assert.Equal(s.Step, InstanceProtocol.HeightMarker(o.Object.Z));
        }
        Assert.Equal(12, level.ServerObjects.Count);
        Assert.Single(level.ServerObjects, p => !p.Sent); // the camp's ProphecyMonsterLocator, server-internal

        // The stats go in before the first generation of (area, seed): a level is kept by (area, seed, level) as generated.
        const uint vaalSeed = 4204881227;
        ServerLevels.PlayableSeed(coast, vaalSeed, 2, new Dictionary<uint, int> { [8213] = 1, [22838] = 1, [5945] = 1 });
        var withSideArea = ServerLevels.For(coast, vaalSeed, 2);
        Assert.NotNull(withSideArea);
        var wayIn = Assert.Single(withSideArea.Exits, e => e.Kind == ExitKind.SideArea);
        Assert.Equal(AreaTransitionObject.MetadataPath, wayIn.Object.Type);
        Assert.Equal("side_cave1_entrance_2.arm", wayIn.Room);
        Assert.DoesNotContain(withSideArea.Entrances, p => p.SideArea);

        const string chateau = "MapWorldsChateau";
        const uint chateauSeed = 2565914107;
        var chateauStats = new Dictionary<uint, int> { [1026] = 6, [1041] = 57, [1246] = 20, [1365] = 1, [1992] = 1, [2501] = 5, [4567] = 1, [6619] = 1, [8888] = 6913, [8892] = 34, [8897] = 1, [10160] = 1, [10959] = 32, [13520] = 110, [13522] = 10, [13523] = 200, [13528] = 10, [13832] = 60, [15680] = 39, [16287] = 1, [16348] = 1, [16349] = 1, [16350] = 1, [16351] = 1, [16354] = 1, [16355] = 1, [16356] = 1, [16370] = 1, [16884] = 18, [16956] = 50, [17650] = 6, [17651] = 10, [17652] = 30, [17681] = -45, [17682] = -35, [17683] = -45, [18763] = 1, [18764] = 1, [19232] = 1, [19305] = 1, [19956] = 40, [19974] = 100, [19994] = 92, [20155] = 1, [20157] = 1, [20158] = 1, [20159] = 1, [20161] = 1, [20162] = 1, [20163] = 1, [20180] = 20, [20182] = 50, [20393] = 20, [20571] = 3, [21179] = 1, [21215] = 1, [21223] = 1, [21408] = 60, [21625] = 110, [22578] = 0, [22739] = 1, [22789] = 250, [22838] = 1, [22896] = 1 };
        Assert.Equal(chateauSeed, ServerLevels.PlayableSeed(chateau, chateauSeed, 68, chateauStats));
        Assert.Equal(2907803743u, ServerLevels.For(chateau, chateauSeed, 68)?.TileHash);
    }
}
