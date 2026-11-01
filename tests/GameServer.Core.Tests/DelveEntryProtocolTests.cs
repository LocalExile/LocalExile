using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public sealed class DelveEntryProtocolTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(123u)]
    public void Trail_lamp_uses_native_radius_power_and_previous_object_link(uint previous)
    {
        using var r = new BinaryReader(new MemoryStream(new DelveTrailLightObject(10, 20, previous).Blob()));
        r.ReadBytes(2);
        Assert.Equal(10, r.ReadInt32()); Assert.Equal(20, r.ReadInt32());
        r.ReadBytes(8); // rest of Positioned
        Assert.Equal(0, r.ReadByte()); // original lamp AO
        Assert.Equal(1, r.ReadByte()); // transitioned
        r.ReadBytes(12); // Stats, Buffs
        r.ReadBytes(55); // Life pools and flag
        Assert.Equal(25f, r.ReadSingle());
        Assert.Equal(previous == 0 ? 0u : 1u, r.ReadUInt32());
        Assert.Equal(1, r.ReadByte()); Assert.Equal(0, r.ReadByte());
        if (previous != 0) Assert.Equal(previous, r.ReadUInt32());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
    }
    [Fact]
    public void Crawler_orientation_uses_native_float_bits_and_unlocks_the_next_turn() =>
        Assert.Equal("01B90000002A0000000000003F80000000", Convert.ToHexString(InstanceProtocol.Orientation(42, 1)));

    [Fact]
    public void Sulphite_fill_refresh_has_network_order_old_and_new_values() =>
        Assert.Equal("022601F401F4", Convert.ToHexString(InstanceDelve.SulphiteChanged(500, 500)));

    [Fact]
    public void Mine_enables_the_shared_upgrade_container_without_changing_the_extra_terrain()
    {
        var packet = new AreaInstance(1, InstanceDelve.MineArea, 1, 0,
            TerrainData: InstanceDelve.FirstMineTerrain(), AchievementSets: [2]).Encode();
        // Empty text: fixed header ends at byte 24, then two empty vec16 lists.
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 20 }, packet[28..34]);
        Assert.Equal(InstanceDelve.FirstMineTerrain(), packet[34..]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AreaInstance(1, 1, 1, 0, AchievementSets: new byte[256]).Encode());
    }

    [Fact]
    public void Native_chart_route_keeps_its_two_distinct_cells_at_every_tcp_boundary()
    {
        byte[] bytes = Convert.FromHexString("0241020000000000000000000000020000000000000000000000000000000200000001");
        var route = InstanceDelve.ReadRoute(bytes);
        Assert.Equal(new InstanceDelve.RoutePoint[] { new(0, 0, 2, 0), new(0, 0, 2, 1) }, route);
        for (int split = 0; split <= bytes.Length; split++)
        {
            var first = InstanceProtocol.SplitClientMessages(bytes.AsSpan(0, split), out var carry, out var unknown);
            Assert.Empty(unknown);
            var second = InstanceProtocol.SplitClientMessages([.. carry, .. bytes.AsSpan(split)], out carry, out unknown);
            Assert.Empty(unknown); Assert.Empty(carry);
            Assert.Equal(bytes, Assert.Single(first.Concat(second)).Bytes);
        }
        Assert.Throws<ArgumentException>(() => InstanceDelve.ReadRoute(bytes[..^1]));
    }

    [Fact]
    public void Generator_uses_the_room_console_art_and_both_interaction_components()
    {
        byte[] blob = new DelveGeneratorObject(376, 352, 5.12494f).Blob();
        Assert.Equal(376, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(2)));
        Assert.Equal(352, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(6)));
        Assert.Equal(1, blob[18]);
        int chars = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(19));
        Assert.Equal(DelveGeneratorObject.Model, System.Text.Encoding.Unicode.GetString(blob, 23, chars * 2));
        Assert.Equal(new byte[3], blob[(23 + chars * 2)..]);
    }

    [Fact]
    public void Equipment_data_preserves_all_ten_levels_and_separate_resource_fields()
    {
        byte[] levels = [1, 2, 3, 4, 5, 0, 0, 7, 8, 9];
        byte[] packet = InstanceDelve.DelveData(1200, 123456, 3, 2, levels);
        Assert.Equal(37, packet.Length);
        Assert.Equal(123456u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(3)));
        Assert.Equal(levels, packet[9..19]);
        Assert.Equal(1200, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(19)));
        Assert.Equal(3, packet[31]); Assert.Equal(2, packet[32]);
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceDelve.DelveData(0, upgrades: new byte[11]));
    }

    [Fact]
    public void Generator_requests_can_be_split_at_any_tcp_boundary()
    {
        byte[] bytes = [0x02, 0x3e, 0x02, 0x29, 0, 8, 0x02, 0x2a, 1, 3];
        for (int split = 0; split <= bytes.Length; split++)
        {
            var first = InstanceProtocol.SplitClientMessages(bytes.AsSpan(0, split), out var carry, out var unknown);
            Assert.Empty(unknown);
            var second = InstanceProtocol.SplitClientMessages([.. carry, .. bytes.AsSpan(split)], out carry, out unknown);
            Assert.Empty(unknown); Assert.Empty(carry);
            Assert.Equal(new ushort[] { 0x23e, 0x229, 0x22a }, first.Concat(second).Select(m => m.Id));
        }
    }

    [Theory]
    [InlineData(DelveCrawlerAttachmentObject.LeftWheel, "aux_L_wheel_attachment", false)]
    [InlineData(DelveCrawlerAttachmentObject.RightWheel, "aux_R_wheel_attachment", false)]
    [InlineData(DelveCrawlerAttachmentObject.Torch, "aux_main_tip", true)]
    public void Crawler_parts_attach_to_the_body_bone_without_consuming_the_following_components(
        string model, string bone, bool light)
    {
        using var reader = new BinaryReader(new MemoryStream(new DelveCrawlerAttachmentObject(408, 310, 123, model, bone, light).Blob()));
        reader.ReadBytes(18); // flags and Positioned
        if (light)
        {
            Assert.Equal(100f / 35, reader.ReadSingle());
            Assert.Equal(1f, reader.ReadSingle()); Assert.Equal(1f, reader.ReadSingle());
        }
        Assert.Equal(5, reader.ReadByte()); // model and attachment
        int chars = reader.ReadInt32();
        Assert.Equal(model, System.Text.Encoding.Unicode.GetString(reader.ReadBytes(chars * 2)));
        Assert.Equal(123u, reader.ReadUInt32());
        Assert.Equal(0f, reader.ReadSingle()); Assert.Equal(0f, reader.ReadSingle());
        int bytes = reader.ReadInt32();
        Assert.Equal(bone, System.Text.Encoding.ASCII.GetString(reader.ReadBytes(bytes)));
        Assert.Equal(0, reader.ReadByte()); Assert.Equal(0, reader.ReadByte());
        Assert.Equal(new byte[12], reader.ReadBytes(12));
        if (light)
        {
            Assert.Equal(1, reader.ReadByte()); // transitioned
            Assert.Equal(new byte[12], reader.ReadBytes(12)); // Stats and Buffs
            Assert.Equal(1u, reader.ReadUInt32()); reader.ReadBytes(50 + 1); // remaining pools and Life flags
            Assert.Equal(60f, reader.ReadSingle()); Assert.Equal(0u, reader.ReadUInt32());
            Assert.Equal(1, reader.ReadByte()); Assert.Equal(0, reader.ReadByte()); // powered, not a robot variation
        }
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    [Fact]
    public void The_interactive_crawler_uses_the_native_map_viewer_components_with_the_robot_model()
    {
        var blob = new DelveMapViewerObject(408, 310, MathF.PI / 2).Blob();
        Assert.Equal(408, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(2)));
        Assert.Equal(310, BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(6)));
        Assert.Equal(1, blob[18]); // Animated override, after flags and the 16-byte Positioned body.
        int chars = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(19));
        Assert.Equal(DelveRobotObject.Model, System.Text.Encoding.Unicode.GetString(blob, 23, chars * 2));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, blob[(23 + chars * 2)..]);
    }

    [Fact]
    public void First_mine_terrain_is_a_bounded_little_endian_block_inside_the_area_packet()
    {
        byte[] terrain = InstanceDelve.FirstMineTerrain();
        Assert.Equal(20, terrain.Length);
        Assert.Equal(0x20, terrain[0]); // Delve extra-data flag.
        Assert.Equal(0x21, terrain[8]); // Width one, height two.
        Assert.Equal(1, terrain[10]); // Mine entrance feature.
        Assert.Equal(7, terrain[15]); // First Azurite quest feature.
        Assert.Equal(0x10, terrain[11] & 0xf0);
        Assert.Equal(0x40, terrain[16] & 0xf0);
        var plain = new AreaInstance(1, InstanceDelve.MineArea, 1, 0).Encode();
        var mine = new AreaInstance(1, InstanceDelve.MineArea, 1, 0, TerrainData: terrain).Encode();
        Assert.Equal(plain.Length + 2 + terrain.Length, mine.Length);
        Assert.Equal(terrain.Length, BinaryPrimitives.ReadUInt16BigEndian(mine.AsSpan(plain.Length)));
        Assert.Equal(terrain, mine[(plain.Length + 2)..]);
        Assert.Single(plain.Zip(mine), pair => pair.First != pair.Second); // Only the extra-data flag changes.
    }

    [Fact]
    public void Oversized_extra_terrain_is_rejected_instead_of_wrapping_the_wire_length() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AreaInstance(1, InstanceDelve.MineArea, 1, 0, TerrainData: new byte[65536]).Encode());
}
