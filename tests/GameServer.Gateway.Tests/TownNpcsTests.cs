using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The campaign towns' NPCs (TownNpcs.cs, 17-npcs.md §2): their rooms' NPC lines through the client's room placement.
/// The tests that read game data (data/tables, data/room-points.json, the Lioneye's Watch walk-map capture) return early
/// without it, as the other catalogue tests do.
/// </summary>
public class TownNpcsTests
{
    static readonly Dictionary<string, (int X, int Y)> MeasuredOnTheMinimap = new()
    {
        ["Metadata/NPC/Act1/Tarkleigh"] = (388, 182),
        ["Metadata/NPC/Act1/Nessa"] = (346, 271),
        ["Metadata/NPC/Act1/Bestel"] = (361, 246),
    };

    /// <summary>Each town room's placement code and origin as the client placed it: its walk-map capture of every town
    /// (seed 1, 2026-10-25, &lt;area&gt;_seed1.rooms.json; not in the repository).</summary>
    public static readonly TheoryData<string, string, int, int, int, uint?> Captured = new()
    {
        { "1_1_town", "Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm", 3, 0, 0, null },
        { "1_2_town", "Metadata/Terrain/Act2/Town/Rooms/forest_town.arm", 3, 0, 92, 1 },
        { "1_3_town", "Metadata/Terrain/Act3/Town/Rooms/city_town.arm", 0, 69, 69, 1 },
        { "1_4_town", "Metadata/Terrain/Act4/Town/Rooms/town.arm", 3, 0, 0, null },
        { "1_5_town", "Metadata/Terrain/Act5/Town/Rooms/town_01.arm", 0, 138, 138, 1 },
        { "2_6_town", "Metadata/Terrain/Act6/BeachTown/Rooms/part2_beach_town.arm", 3, 0, 0, null },
        { "2_7_town", "Metadata/Terrain/Act7/Town/Rooms/broken_bridge_town.arm", 0, 437, 207, 1 },
        { "2_8_town", "Metadata/Terrain/Act8/Town/Rooms/city_town.arm", 0, 69, 69, 1 },
        { "2_9_town", "Metadata/Terrain/Act9/Town/Rooms/town.arm", 3, 0, 0, null },
        { "2_10_town", "Metadata/Terrain/Act10/Town/Rooms/dock_town.arm", 0, 0, 0, null },
        { "2_11_town", "Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 0, 299, 299, 1 },
        { "2_11_town", "Metadata/Terrain/Act11/Town/Rooms/Unique/entrance_04.arm", 0, 161, 851, 1 },
        { "2_11_endgame_town", "Metadata/Terrain/Act11/Camp/Rooms/camp.arm", 0, 92, 92, 1 },
    };

    [Theory]
    [MemberData(nameof(Captured))]
    public void Each_room_has_the_clients_placement(string area, string arm, int code, int x, int y, uint? seed)
    {
        var room = TownNpcs.All.Single(t => t.AreaId == area).Parts.Single(p => p.Room.Arm == arm).Room;
        Assert.Equal((code, x, y, seed), (room.Code, room.OffsetX, room.OffsetY, room.Seed));
    }

    [Fact]
    public void Every_room_is_listed_with_its_capture()
    {
        var listed = TownNpcs.All.SelectMany(t => t.Parts.Select(p => (t.AreaId, p.Room.Arm))).ToHashSet();
        var captured = Captured.Select(row => ((string)row[0], (string)row[1])).ToHashSet();
        Assert.Equal(captured.Order(), listed.Order());
    }

    [Fact]
    public void R270_places_a_room_cell_at_y_and_width_minus_one_minus_x()
    {
        var room = new TownNpcs.Room("r.arm", 414, 690, 3);
        Assert.Equal((389, 183), room.Cell(230, 389));
        Assert.Equal((0, 413), room.Cell(0, 0));
        Assert.Equal(3 * MathF.PI / 2 - 0.155698f, room.Angle(-0.155698f), 4);
        // I keeps the cell (plus the room's origin) and the angle, wrapped into [0, 2π).
        var plain = new TownNpcs.Room("r.arm", 100, 100, 0, 10, 20);
        Assert.Equal((15, 27), plain.Cell(5, 7));
        Assert.Equal(2 * MathF.PI - 1, plain.Angle(-1), 4);
    }

    [Fact]
    public void Lioneyes_Watch_lands_within_a_cell_of_its_minimap_measurement()
    {
        var placed = NpcCatalog.In(AreaLayouts.LioneyesWatch, 1);
        Assert.Equal(MeasuredOnTheMinimap.Keys.Order(), placed.Select(p => p.Id).Order());
        foreach (var p in placed)
        {
            var (x, y) = MeasuredOnTheMinimap[p.Id];
            Assert.InRange(p.X - x, -1, 1);
            Assert.InRange(p.Y - y, -1, 1);
            Assert.True(p.FromRoom);
        }
        Assert.Equal("Metadata/NPC/Act1/Tarkleigh", placed[0].Id);
        Assert.Equal((389, 183), (placed[0].X, placed[0].Y));
    }

    [Fact]
    public void Every_campaign_town_has_its_npcs_inside_its_rooms()
    {
        Assert.Equal(12, TownNpcs.All.Length);
        Assert.Equal(TownNpcs.All.Length, TownNpcs.All.Select(t => t.Area).Distinct().Count());
        Assert.Equal(54, TownNpcs.All.Sum(t => t.Npcs.Count()));
        foreach (var town in TownNpcs.All)
        {
            foreach (var part in town.Parts)
            {
                var room = part.Room;
                var (w, h) = room.Code % 2 == 1 ? (room.Height, room.Width) : (room.Width, room.Height);
                Assert.NotEmpty(part.Npcs);
                foreach (var p in part.Placements())
                {
                    Assert.InRange(p.X, room.OffsetX, room.OffsetX + w - 1);
                    Assert.InRange(p.Y, room.OffsetY, room.OffsetY + h - 1);
                    Assert.InRange(p.Rotation, 0, 2 * MathF.PI);
                }
            }
            Assert.Equal(town.Npcs.Count(), NpcCatalog.In(town.Area, 1).Count);
        }
    }

    [Fact]
    public void Rooms_placed_with_another_seed_are_left_out_with_a_note()
    {
        var notes = new List<string>();
        Assert.Empty(NpcCatalog.In(0xf580, 7, notes));   // Oriath: both rooms were captured with seed 1
        Assert.Equal(2, notes.Count);
        Assert.Contains("seed 1, not 7", notes[0]);
        // Rooms at (0, 0) do not depend on the seed.
        Assert.Equal(3, NpcCatalog.In(AreaLayouts.LioneyesWatch, 7, notes).Count);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void Npc_blob_carries_a_room_model_and_height()
    {
        byte[] blob = new NpcObject(1, 2, NpcObject.BaseComponents, Model: "a.ao", Height: -335.938f).Blob();
        Assert.Equal(
            "0000"                                       // object flags, base data
            + "01000000" + "02000000" + "00000000"       // Positioned: x, y, rotation 0
            + "0000" + "04" + "02"                       // team, flags0 0x04 (fixed height), flags1 0x02
            + "2B000000"                                 // i32 −z / 7.8125 = +43 (world z points down)
            + Convert.ToHexString(BitConverter.GetBytes(-335.938f))   // f32 z
            + "0001"                                     // flags1 0x02's two bytes
            + "01" + "04000000" + "61002E0061006F00"     // Animated: bit0, u32 length, UTF-16 "a.ao"
            + "00" + "01FF" + "00"                       // InteractionAction, NPC, MinimapIcon
            + "00",                                      // tasks
            Convert.ToHexString(blob));
    }

    /// <summary>Each row copies a line of its room: the same NPC type at the same room cell in data/room-points.json
    /// (scripts/re/atlas room-points, which keeps every town room with its server objects).</summary>
    [Fact]
    public void Every_row_is_a_line_of_its_room()
    {
        if (FindUp("data/room-points.json") is not { } file) return;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var rooms = doc.RootElement.GetProperty("rooms");
        foreach (var town in TownNpcs.All)
            foreach (var part in town.Parts)
            {
                if (!rooms.TryGetProperty(part.Room.Arm, out var room)) continue;   // room-points keeps rooms with points
                var lines = room.GetProperty("doodads").EnumerateArray()
                    .Where(d => d.TryGetProperty("type", out var t) && t.GetString() is not null)
                    .Select(d => (d.GetProperty("type").GetString(), (int)d.GetProperty("x").GetDouble(), (int)d.GetProperty("y").GetDouble()))
                    .ToHashSet();
                foreach (var n in part.Npcs) Assert.Contains((n.Id, n.X, n.Y), lines);
            }
    }

    /// <summary>The NPC lines of the town rooms as the game files write them (Client/Content.ggpk, 3.29.3b; the room's
    /// version decides where the angle is). The tests cannot read the bundles (they need the Node Oodle decoder), so the
    /// lines are copied here verbatim: <c>x y [0] angle qx qy qz qw a b flag [z] scale "model.ao" "type"</c>, where
    /// versions 35 and 36 have the 0 before the angle and version 31 (Oriath's entrance_04.arm) does not.</summary>
    static readonly object[][] RoomLines =
    [
        ["Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm", 36, "230 389 0 -0.155698 -0 0 -0.0777702 0.996971 0 0 0 1 \"Metadata/NPC/Act1/Tarkleigh.ao\" \"Metadata/NPC/Act1/Tarkleigh\""],
        ["Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm", 36, "143 346 0 0.168087 0 0 0.0839448 0.99647 0 0 1 -231.997 1 \"Metadata/NPC/Act1/Nessa.ao\" \"Metadata/NPC/Act1/Nessa\""],
        ["Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm", 36, "166 362 0 0.0872663 0 0 0.0436193 0.999048 0 0 0 1 \"Metadata/NPC/Act1/Bestel.ao\" \"Metadata/NPC/Act1/Bestel\""],
        ["Metadata/Terrain/Act2/Town/Rooms/forest_town.arm", 36, "144 153 0 2.44345 0 0 0.939691 0.342025 1 0 0 1 \"Metadata/NPC/Act2/Yeena.ao\" \"Metadata/NPC/Act2/Yeena\""],
        ["Metadata/Terrain/Act2/Town/Rooms/forest_town.arm", 36, "204 159 0 2.04085 0 0 0.852331 0.523003 0 0 1 -15.625 1 \"Metadata/NPC/Act2/Eramir.ao\" \"Metadata/NPC/Act2/Eramir\""],
        ["Metadata/Terrain/Act2/Town/Rooms/forest_town.arm", 36, "214 203 0 1.19238 0.000272958 0.000440131 0.561495 0.82748 0 0 0 1 \"Metadata/NPC/Act2/Greust.ao\" \"Metadata/NPC/Act2/Greust\""],
        ["Metadata/Terrain/Act2/Town/Rooms/forest_town.arm", 36, "144 179 0 0.872664 0 0 0.422618 0.906308 0 0 0 1 \"Metadata/NPC/Act2/Silk.ao\" \"Metadata/NPC/Act2/Silk\""],
        ["Metadata/Terrain/Act2/Town/Rooms/forest_town.arm", 36, "134 168 0 1.309 0 0 0.608761 0.793354 0 0 0 1 \"Metadata/NPC/Act2/Helena.ao\" \"Metadata/NPC/Act2/HelenaTown\""],
        ["Metadata/Terrain/Act3/Town/Rooms/city_town.arm", 36, "303 402 0 -0.492313 0 0 -0.243678 0.969856 0 0 1 -18.3426 1 \"Metadata/NPC/Act3/Hargan.ao\" \"Metadata/NPC/Act3/Hargan\""],
        ["Metadata/Terrain/Act3/Town/Rooms/city_town.arm", 36, "149 433 0 -0.360166 0 0 -0.179111 0.983829 0 0 1 54.6875 1 \"Metadata/NPC/Act3/Grigor.ao\" \"Metadata/NPC/Act3/Grigor\""],
        ["Metadata/Terrain/Act3/Town/Rooms/city_town.arm", 36, "305 350 0 -1.36175 -0.000845728 -2.99645e-10 -0.629474 0.777021 1 0 1 -62.5 1 \"Metadata/NPC/Act3/Maramoa.ao\" \"Metadata/NPC/Act3/Maramoa\""],
        ["Metadata/Terrain/Act3/Town/Rooms/city_town.arm", 36, "179 376 0 -0.985034 0 0 -0.472845 0.881146 1 0 1 54.6875 1 \"Metadata/NPC/Act3/Clarissa.ao\" \"Metadata/NPC/Act3/ClarissaTown\""],
        ["Metadata/Terrain/Act4/Town/Rooms/town.arm", 36, "252 160 0 2.82952 0 0 0.987851 0.155404 0 0 1 -335.938 1 \"Metadata/NPC/Act4/Kira.ao\" \"Metadata/NPC/Act4/Kira\""],
        ["Metadata/Terrain/Act4/Town/Rooms/town.arm", 36, "260 566 0 -0.469922 0 0 -0.232805 0.972523 0 0 1 -695.313 1 \"Metadata/NPC/Act4/Oyun.ao\" \"Metadata/NPC/Act4/Oyun\""],
        ["Metadata/Terrain/Act4/Town/Rooms/town.arm", 36, "206 213 0 0.520564 0.000272958 -0.00012207 0.257353 0.966317 0 0 1 -335.938 1 \"Metadata/NPC/Act4/Petarus.ao\" \"Metadata/NPC/Act4/PetarusVanja\""],
        ["Metadata/Terrain/Act4/Town/Rooms/town.arm", 36, "253 555 0 1.11073 0 0 0.527252 0.849709 0 0 1 -695.313 1 \"Metadata/NPC/Act3/Dialla.ao\" \"Metadata/NPC/Act4/DiallaTown\""],
        ["Metadata/Terrain/Act4/Town/Rooms/town.arm", 36, "311 407 0 0.00824316 0 0 0.00412157 0.999991 0 0 1 -392.663 1 \"Metadata/NPC/Act4/Tasuni.ao\" \"Metadata/NPC/Act4/Tasuni\""],
        ["Metadata/Terrain/Act5/Town/Rooms/town_01.arm", 36, "214 180 0 -0.144973 -0 0 -0.0724228 0.997374 0 0 0 1 \"Metadata/NPC/Act5/Utula.ao\" \"Metadata/NPC/Act5/Utula\""],
        ["Metadata/Terrain/Act5/Town/Rooms/town_01.arm", 36, "281 210 0 -1.0472 -0 0 -0.5 0.866025 0 0 1 -653.872 1 \"Metadata/NPC/Act5/Bannon.ao\" \"Metadata/NPC/Act5/BannonTown\""],
        ["Metadata/Terrain/Act5/Town/Rooms/town_01.arm", 36, "232 172 0 -0.785399 -0 0 -0.382684 0.923879 0 0 0 1 \"Metadata/NPC/Act5/Lani.ao\" \"Metadata/NPC/Act5/Lani\""],
        ["Metadata/Terrain/Act5/Town/Rooms/town_01.arm", 36, "275 178 0 -1.5708 0 0 -0.707107 0.707107 1 0 0 1 \"Metadata/NPC/Act5/Vilenta.ao\" \"Metadata/NPC/Act5/Vilenta\""],
        ["Metadata/Terrain/Act6/BeachTown/Rooms/part2_beach_town.arm", 36, "230 390 0 -0 -0 0 -0 1 0 0 0 1 \"Metadata/NPC/Act1/Tarkleigh.ao\" \"Metadata/NPC/Act6/Tarkleigh\""],
        ["Metadata/Terrain/Act6/BeachTown/Rooms/part2_beach_town.arm", 36, "166 360 0 0.0872663 0 0 0.0436193 0.999048 0 0 0 1 \"Metadata/NPC/Act1/Bestel.ao\" \"Metadata/NPC/Act6/Bestel\""],
        ["Metadata/Terrain/Act6/BeachTown/Rooms/part2_beach_town.arm", 36, "301 263 0 -0.0170866 -0 0 -0.00854318 0.999964 0 0 1 -332.881 1 \"Metadata/NPC/Act5/Sin.ao\" \"Metadata/NPC/Act6/SinTown\""],
        ["Metadata/Terrain/Act6/BeachTown/Rooms/part2_beach_town.arm", 36, "408 229 0 -1.57466 0 0 -0.708471 0.70574 0 0 1 -83.5598 1 \"Metadata/NPC/Act6/Lilly.ao\" \"Metadata/NPC/Act6/Lilly\""],
        ["Metadata/Terrain/Act7/Town/Rooms/broken_bridge_town.arm", 36, "136 365 0 -2.25908 0 0 0.904215 -0.427078 0 0 0 1 \"Metadata/NPC/Act7/Eramir.ao\" \"Metadata/NPC/Act7/Eramir\""],
        ["Metadata/Terrain/Act7/Town/Rooms/broken_bridge_town.arm", 36, "129 425 0 -0.387718 -0 0 -0.192647 0.981268 1 0 1 -429.688 1 \"Metadata/NPC/Act7/Yeena.ao\" \"Metadata/NPC/Act7/YeenaTown\""],
        ["Metadata/Terrain/Act7/Town/Rooms/broken_bridge_town.arm", 36, "149 419 0 0.261799 0 0 0.130526 0.991445 0 0 1 -432.065 1 \"Metadata/NPC/Act7/Helena.ao\" \"Metadata/NPC/Act7/Helena\""],
        ["Metadata/Terrain/Act7/Town/Rooms/broken_bridge_town.arm", 36, "143 202 0 -2.18261 0 0 0.887229 -0.461329 0 0 1 -433.084 1 \"Metadata/NPC/Act5/Sin.ao\" \"Metadata/NPC/Act7/SinTown\""],
        ["Metadata/Terrain/Act7/Town/Rooms/broken_bridge_town.arm", 36, "95 203 0 1.00597 0 0 0.482045 0.876146 0 0 0 1 \"Metadata/NPC/Act6/RotTooth.ao\" \"Metadata/NPC/Act7/Weylam\""],
        ["Metadata/Terrain/Act8/Town/Rooms/city_town.arm", 36, "304 402 0 -0.492313 -0 0 -0.243678 0.969856 0 0 0 1 \"Metadata/NPC/Act3/Hargan.ao\" \"Metadata/NPC/Act8/Hargan\""],
        ["Metadata/Terrain/Act8/Town/Rooms/city_town.arm", 36, "304 349 0 -1.36175 -0.000845728 -2.24734e-10 -0.629474 0.777021 1 0 0 1 \"Metadata/NPC/Act3/Maramoa.ao\" \"Metadata/NPC/Act8/Maramoa\""],
        ["Metadata/Terrain/Act8/Town/Rooms/city_town.arm", 36, "170 382 0 -0.488777 -0 0 -0.241963 0.970285 1 0 0 1 \"Metadata/NPC/Act3/Clarissa.ao\" \"Metadata/NPC/Act8/ClarissaTown\""],
        ["Metadata/Terrain/Act8/Town/Rooms/city_town.arm", 36, "205 306 0 1.64849 0 0 0.734034 0.679113 0 0 0 1 \"Metadata/NPC/Act5/Sin.ao\" \"Metadata/NPC/Act8/SinTown\""],
        ["Metadata/Terrain/Act9/Town/Rooms/town.arm", 36, "231 160 0 2.61603 0.000272958 0.00012207 0.965671 0.259768 0 0 1 -335.938 1 \"Metadata/NPC/Act4/Petarus.ao\" \"Metadata/NPC/Act9/PetarusVanja\""],
        ["Metadata/Terrain/Act9/Town/Rooms/town.arm", 36, "200 182 0 2.04771 0 0 0.854119 0.520078 0 0 1 -335.938 1 \"Metadata/NPC/Act9/Irasha/Irasha.ao\" \"Metadata/NPC/Act9/Irasha\""],
        ["Metadata/Terrain/Act9/Town/Rooms/town.arm", 36, "311 407 0 0.00824316 0 0 0.00412157 0.999991 0 0 1 -392.663 1 \"Metadata/NPC/Act4/Tasuni.ao\" \"Metadata/NPC/Act9/Tasuni\""],
        ["Metadata/Terrain/Act9/Town/Rooms/town.arm", 36, "297 184 0 -0.540068 -0 0 -0.266764 0.963762 0 0 1 -319.633 1 \"Metadata/NPC/Act5/Sin.ao\" \"Metadata/NPC/Act9/SinTown\""],
        ["Metadata/Terrain/Act10/Town/Rooms/dock_town.arm", 36, "490 275 0 -2.79379 0 0 0.984917 -0.173028 0 0 1 -86.9565 1 \"Metadata/NPC/Act5/Sin.ao\" \"Metadata/NPC/Act10/SinTown\""],
        ["Metadata/Terrain/Act10/Town/Rooms/dock_town.arm", 36, "470 295 0 -0.497837 -0 0 -0.246356 0.969179 0 0 0 1 \"Metadata/NPC/Act10/Bannon.ao\" \"Metadata/NPC/Act10/BannonTown\""],
        ["Metadata/Terrain/Act10/Town/Rooms/dock_town.arm", 36, "457 356 0 -1.08016 -0 0 -0.514205 0.857667 0 0 0 1 \"Metadata/NPC/Act6/RotTooth.ao\" \"Metadata/NPC/Act10/Weylam\""],
        ["Metadata/Terrain/Act10/Town/Rooms/dock_town.arm", 36, "431 274 0 2.71676 0 0 0.977524 0.210824 0 0 0 1 \"Metadata/NPC/Act10/Lani.ao\" \"Metadata/NPC/Act10/Lani\""],
        ["Metadata/Terrain/Act10/Town/Rooms/dock_town.arm", 36, "448 364 0 -0.0830655 -0 0 -0.0415208 0.999138 0 0 0 1 \"Metadata/NPC/Act5/Lilly.ao\" \"Metadata/NPC/Act10/Lilly\""],
        ["Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 35, "327 417 0 -1.0472 -0 0 -0.5 0.866025 0 0 0 1 \"Metadata/NPC/Missions/StrDexIntEpilogue.ao\" \"Metadata/NPC/Epilogue/Zana\""],
        ["Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 35, "231 453 0 0.888412 0 0 0.429741 0.902952 0 0 0 1 \"Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine.ao\" \"Metadata/NPC/Epilogue/Innocence\""],
        ["Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 35, "252 457 0 -0.832619 -0 0 -0.404388 0.914588 0 0 0 1 \"Metadata/NPC/Act5/Sin.ao\" \"Metadata/NPC/Epilogue/Sin\""],
        ["Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 35, "344 359 0 -1.83259 0 0 -0.793353 0.608762 0 0 0 1 \"Metadata/NPC/Act6/RotTooth.ao\" \"Metadata/NPC/Epilogue/Weylam\""],
        ["Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 35, "339 369 0 -0.523599 -0 0 -0.258819 0.965926 0 0 0 1 \"Metadata/NPC/Act5/Lilly.ao\" \"Metadata/NPC/Epilogue/Lilly\""],
        ["Metadata/Terrain/Act11/Town/Rooms/Unique/fountain.arm", 35, "333 343 0 -2.09439 0 0 -0.866025 0.500001 0 0 0 1 \"Metadata/NPC/Act10/Lani.ao\" \"Metadata/NPC/Epilogue/Lani\""],
        ["Metadata/Terrain/Act11/Town/Rooms/Unique/entrance_04.arm", 31, "389 175 0.742503 0 0 0.362782 0.931874 0 0 0 1 \"Metadata/NPC/Missions/Helena.ao\" \"Metadata/NPC/Epilogue/Helena\""],
        ["Metadata/Terrain/Act11/Camp/Rooms/camp.arm", 36, "186 366 0 0.499106 0 0 0.246971 0.969023 0 0 1 -202.5 1 \"Metadata/NPC/Act5/Lilly.ao\" \"Metadata/NPC/Epilogue/Lilly\""],
        ["Metadata/Terrain/Act11/Camp/Rooms/camp.arm", 36, "701 784 0 -0.879269 0 0 -0.425609 0.904907 0 0 0 1 \"Metadata/NPC/Act6/RotTooth.ao\" \"Metadata/NPC/Epilogue/Weylam\""],
        ["Metadata/Terrain/Act11/Camp/Rooms/camp.arm", 36, "712 778 0 -0.355253 0 0 -0.176694 0.984266 0 0 0 1 \"Metadata/NPC/Act10/Lani.ao\" \"Metadata/NPC/Epilogue/Lani\""],
        ["Metadata/Terrain/Act11/Camp/Rooms/camp.arm", 36, "771 696 0 0.564095 0 0 0.278323 0.960488 0 0 0 1 \"Metadata/NPC/Missions/Helena.ao\" \"Metadata/NPC/Epilogue/Helena\""],
    ];

    /// <summary>Every row against its room's own line: cell, angle (also the quaternion's turn), z, and the model (a
    /// row's model is the line's; a row without one relies on its type's model, which is the line's: checked against the
    /// .ot chains when the table was written).</summary>
    [Fact]
    public void Every_row_matches_its_room_line()
    {
        var rows = TownNpcs.All.SelectMany(t => t.Parts.SelectMany(p => p.Npcs.Select(n => (p.Room.Arm, n)))).ToList();
        Assert.Equal(RoomLines.Length, rows.Count);
        foreach (var entry in RoomLines)
        {
            string arm = (string)entry[0], line = (string)entry[2];
            int version = (int)entry[1];
            var quoted = Regex.Matches(line, @"""([^""]*)""");
            string model = quoted[0].Groups[1].Value, type = quoted[1].Groups[1].Value;
            float[] v = [.. line[..line.IndexOf('"')].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture))];
            int a = version >= 35 ? 3 : 2;
            var row = rows.Single(r => r.Arm == arm && r.n.Id == type && r.n.X == (int)v[0] && r.n.Y == (int)v[1]).n;
            string where = $"{arm} {type}";
            Assert.True(Math.Abs(v[a] - row.Angle) < 1e-4, $"{where}: angle {v[a]} vs {row.Angle}");
            float turn = 2 * MathF.Atan2(v[a + 3], v[a + 4]);   // quaternion (0, 0, qz, qw)
            Assert.True(Math.Abs(Math.IEEERemainder(turn - v[a], 2 * Math.PI)) < 1e-3, $"{where}: quaternion {turn} vs {v[a]}");
            float? z = v[a + 7] == 1 ? v[a + 8] : null;
            Assert.True(z == row.Z, $"{where}: z {z} vs {row.Z}");
            if (row.Model is not null) Assert.Equal(model, row.Model);
        }
    }

    /// <summary>The client's own placement of Lioneye's Watch's room (its walk-map capture): every plain doodad of
    /// beach_town_unique_normal.arm stands where <see cref="TownNpcs.Room.Cell"/> puts its line.</summary>
    [Fact]
    public void The_room_placement_is_the_clients_in_Lioneyes_Watch()
    {
        if (FindUp("data/room-points.json") is not { } file || FindUp("data/walkability/1_1_town_seed1.doodads.json") is not { } capture) return;
        var room = TownNpcs.Of(AreaLayouts.LioneyesWatch)!.Parts[0].Room;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        using var cap = JsonDocument.Parse(File.ReadAllBytes(capture));
        var models = cap.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetString()!.ToLowerInvariant()).ToArray();
        var placed = cap.RootElement.GetProperty("doodads").EnumerateArray()
            .Select(d => (models[d[0].GetInt32()], d[1].GetInt32(), d[2].GetInt32())).ToHashSet();
        var plain = doc.RootElement.GetProperty("rooms").GetProperty(room.Arm).GetProperty("doodads").EnumerateArray()
            .Where(d => !d.TryGetProperty("type", out var t) || t.GetString() is null).ToList();
        Assert.True(plain.Count > 20);
        int found = plain.Count(d =>
        {
            var (x, y) = room.Cell((int)d.GetProperty("x").GetDouble(), (int)d.GetProperty("y").GetDouble());
            return placed.Contains((d.GetProperty("model").GetString()!.ToLowerInvariant(), x, y));
        });
        Assert.Equal(plain.Count, found);
    }

    /// <summary>Every placed NPC resolves to a row the Gateway can create, with dialogue: an Id without its own .ot takes
    /// its Metadata's components (Act 6's Tarkleigh on Act 1's); /npc gets the room's model for Irasha.</summary>
    [Fact]
    public void Every_placed_npc_can_be_created_and_talks()
    {
        if (FindUp("data/tables/NPCs.json") is not { } table || FindUp("data/object-types.json") is null) return;
        var catalog = NpcCatalog.Load(Path.GetDirectoryName(Path.GetDirectoryName(table))!);
        Assert.Null(catalog.Error);
        foreach (var town in TownNpcs.All)
            foreach (var p in town.Placements(1))
            {
                var npc = catalog.Place(p);
                Assert.True(npc is { Supported: true }, $"{town.AreaId}: {p.Id}");
                if (p.Model is not null) Assert.Equal(p.Model, npc!.Model);
                Assert.Equal(p.Height, npc!.Height);
                Assert.True(catalog.TalkOf(npc, 0) is not null, $"{town.AreaId}: {p.Id} has no NPCTalk rows");
            }
        Assert.Equal("Tarkleigh", catalog.Find("Metadata/NPC/Act6/Tarkleigh")!.Name);
        Assert.Equal("Metadata/NPC/Act9/Irasha/Irasha.ao", catalog.Find("Irasha")!.Model);
        Assert.Null(catalog.Find("Metadata/NPC/Act1/Tarkleigh")!.Model);
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}
