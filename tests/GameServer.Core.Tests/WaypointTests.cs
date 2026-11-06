using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class WaypointTests
{
    [Fact]
    public void Waypoints_message_is_a_u32_and_a_19_byte_bitset()
    {
        // S→C 0x4c (read 0x14219f460): u32 BE, then 19 raw bytes; bit i = the i-th waypoint area.
        byte[] m = InstanceWaypoints.Waypoints([0, 1, 9, 89]);
        Assert.Equal(2 + 4 + 19, m.Length);
        Assert.Equal("004C" + "00000000" + "03" + "02" + new string('0', 18) + "02" + new string('0', 14),Convert.ToHexString(m));
    }

    [Fact]
    public void Waypoints_message_ignores_bits_outside_the_bitset()
    {
        Assert.Equal(InstanceWaypoints.Waypoints([]), InstanceWaypoints.Waypoints([-1, 152, 1000]));
    }

    [Fact]
    public void Open_world_panel_is_ui_kind_0_with_four_big_endian_u32s()
    {
        // S→C 0x7b (read 0x1421a0450): u8 kind, then four u32 BE.
        Assert.Equal("007B" + "00" + "00002F00" + "00000000" + "00000000" + "00000000",
            Convert.ToHexString(InstanceWaypoints.OpenUi(InstanceWaypoints.WorldPanelUi, 0x2f00)));
    }

    [Fact]
    public void Open_crafting_bench_selects_the_ordinary_bench_type_not_the_object_id()
    {
        // Client 0x142163420 case 0x33 forwards the first u32 as CraftingBenchTypes row.
        Assert.Equal("007B3300000000000000000000000000000000",
            Convert.ToHexString(InstanceWaypoints.OpenUi(InstanceWaypoints.CraftingBenchUi)));
    }

    [Fact]
    public void Travel_request_reads_area_u32_and_ctrl()
    {
        // C→S 0x4d (write 0x141f92e60): u16 BE area HASH16, u32 BE, u8 Ctrl.
        byte[] m = Convert.FromHexString("004D" + "7AD2" + "00002F00" + "01");
        Assert.Equal(InstanceWaypoints.TravelLength, m.Length);
        Assert.Equal(new InstanceWaypoints.Travel(0x7ad2, 0x2f00, true), InstanceWaypoints.ReadTravel(m));
        Assert.Equal(InstanceWaypoints.TravelLength, InstanceProtocol.KnownClientLength(InstanceWaypoints.TravelId));
    }

    [Fact]
    public void Waypoint_blob_layout()
    {
        // Positioned 16 (+ 8 with the fixed height), Animated, HideoutDoodad (nothing), MinimapIcon, InteractionAction,
        // StateMachine (1 state, 0 timers, compact "active" + flags), tasks.
        const string model = "Metadata/Terrain/Beach/Town_v2/beachencampment_v01_01_v2_waypoint1.ao";
        byte[] b = new WaypointObject(256, 169, model, Rotation: 0, Height: -357.52f).Blob();
        string hex = Convert.ToHexString(b);
        Assert.StartsWith("0000" + "00010000" + "A9000000" + "00000000" + "0000" + "04" + "00" + "2D000000", hex); // marker −z / 7.8125 = 45.76 → 45
        int afterPositioned = 2 + 16 + 8;
        Assert.Equal(1, b[afterPositioned]);
        Assert.Equal((uint)model.Length, BitConverter.ToUInt32(b, afterPositioned + 1));
        int afterAnimated = afterPositioned + 1 + 4 + model.Length * 2;
        Assert.Equal("00" + "00" + "01" + "00" + "00" + "00" + "00", Convert.ToHexString(b.AsSpan(afterAnimated)));
        Assert.Equal(afterAnimated + 7, b.Length);
    }

    [Fact]
    public void Waypoint_without_model_or_height()
    {
        Assert.Equal("0000" + "0A000000" + "14000000" + "00000000" + "0000" + "00" + "00" + "00" + "00" + "00" + "0100" + "01" + "00" + "00",
            Convert.ToHexString(new WaypointObject(10, 20, Active: 1).Blob()));
    }

    [Fact]
    public void Waypoint_areas_are_the_has_waypoint_rows_of_kinds_1_2_5_11_in_table_order()
    {
        string path = Path.Combine(Path.GetTempPath(), $"WorldAreas-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            [{"Id":"1_1_1","Name":"The Twilight Strand","IsTown":false,"HasWaypoint":false,"HASH16":1,"__col51":2},
             {"Id":"1_1_town","Name":"Lioneye's Watch","IsTown":true,"HasWaypoint":true,"HASH16":9052,"__col51":1},
             {"Id":"1_1_2","Name":"The Coast","IsTown":false,"HasWaypoint":true,"HASH16":31442,"__col51":2},
             {"Id":"HideoutX","Name":"A hideout","IsTown":false,"HasWaypoint":true,"HASH16":5,"__col51":16},
             {"Id":"Labyrinth_Airlock","Name":"Aspirants' Plaza","IsTown":false,"HasWaypoint":true,"HASH16":65071,"__col51":11}]
            """);
        try
        {
            var w = WorldAreaWaypoints.Load(path);
            Assert.Equal(["1_1_town", "1_1_2", "Labyrinth_Airlock"], w.Entries.Select(e => e.Id));
            Assert.Equal(1, w.BitOf(0x7ad2));
            Assert.Null(w.BitOf(1));
            Assert.True(w.Find(0x235c)!.IsTown);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_real_table_has_90_waypoints_from_lioneyes_watch_to_aspirants_plaza()
    {
        string? table = FindUp("data/tables/WorldAreas.json");
        if (table is null) return;   // needs the extracted game data
        var w = WorldAreaWaypoints.Load(table);
        Assert.Equal(90, w.Entries.Count);
        Assert.Equal(("1_1_town", 0), (w.Entries[0].Id, w.BitOf(0x235c)));
        Assert.Equal(1, w.BitOf(0x7ad2));                 // The Coast
        Assert.Equal(9, w.BitOf(0x2233));                 // The Forest Encampment
        Assert.Equal("Labyrinth_Airlock", w.Entries[89].Id);
    }

    [Fact]
    public void Quest_flags_are_sorted_64_flag_blocks()
    {
        // u16 count, then (u8 block, u64 LE bits): flags 0 and 5 in block 0, 1423 = block 22 bit 15.
        var w = new BlobWriter();
        PlayerObject.WriteFlagSet(w, [1423, 5, 0, 5, -1, 16384]);
        Assert.Equal("0200" + "00" + "2100000000000000" + "16" + "0080000000000000", Convert.ToHexString(w.ToArray()));
    }

    [Fact]
    public void Quest_flags_update_is_player_component_flag_2()
    {
        byte[] m = InstanceEntities.PlayerQuestFlagsUpdate(0x1000, [0]);
        Assert.EndsWith("0200" + "0100" + "00" + "0100000000000000", Convert.ToHexString(m));
        Assert.Equal(InstanceEntities.ComponentUpdate(0x1000, PlayerObject.PlayerComponentIndex,
            Convert.FromHexString("0200" + "0100" + "00" + "0100000000000000")), m);
    }

    [Fact]
    public void Player_blob_carries_the_quest_flags()
    {
        var none = new PlayerObject("A", 1, 0, 0, 10, 10).Blob();
        var some = new PlayerObject("A", 1, 0, 0, 10, 10, QuestFlags: [0]).Blob();
        Assert.Equal(none.Length + 9, some.Length);
    }

    [Fact]
    public void Waypoint_areas_carry_their_access_flags()
    {
        string? table = FindUp("data/tables/WorldAreas.json");
        if (table is null) return;
        var w = WorldAreaWaypoints.Load(table);
        Assert.Equal([0], w.Find(0x235c)!.AccessFlags);     // a1q1-KilledHillock
        Assert.Equal([24], w.Find(0x3275)!.AccessFlags);    // The Submerged Passage: a1q4-PlacedGlyphs
    }

    static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }
}
