using System.Text.Json;
using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GracePeriodTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Entry_grants_protection_only_in_combat_areas(bool town, bool hideout, bool expected)
    {
        var grace = new GracePeriod();
        Assert.Equal(expected, grace.Start(12, (100, 200), 7, town, hideout));
        Assert.Equal(expected, grace.Protects(12, (100, 200)));
    }

    [Fact]
    public void Exact_deadline_expires_once_and_does_not_refresh_on_idle_checks()
    {
        var grace = new GracePeriod();
        grace.Start(10, (1, 2), 7, false, false);
        Assert.True(grace.Protects(69.999, (1, 2)));
        Assert.Null(grace.End(69.999, (1, 2)));
        Assert.False(grace.Protects(70, (1, 2)));
        Assert.Equal((ushort)7, grace.End(70, (1, 2)));
        Assert.Null(grace.End(71, (1, 2)));
    }

    [Fact]
    public void One_actual_step_removes_protection_even_before_the_remove_packet_tick()
    {
        var grace = new GracePeriod();
        grace.Start(0, (100, 200), 9, false, false);
        Assert.True(grace.Protects(1, (100, 200)));
        Assert.False(grace.Protects(1.01, (101, 200)));
        Assert.False(grace.Protects(1.02, (100, 200))); // returning cannot reactivate it
        Assert.Equal((ushort)9, grace.End(1.02, (100, 200)));
        Assert.Null(grace.End(2, (100, 200), action: true));
    }

    [Fact]
    public void Walk_order_keeps_protection_until_the_walker_takes_its_first_grid_step()
    {
        var walk = new PlayerWalk(100, 100, 37);
        var grace = new GracePeriod();
        grace.Start(0, walk.Position(0), 4, false, false);
        walk.Order(110, 100, 0);
        Assert.True(grace.Protects(0, walk.Position(0)));
        Assert.False(grace.Protects(1, walk.Position(1)));
        Assert.Equal((ushort)4, grace.End(1, walk.Position(1)));
    }

    [Fact]
    public void Skill_or_flask_action_cancels_while_stationary_and_new_entry_gets_fresh_duration()
    {
        var grace = new GracePeriod();
        grace.Start(0, (0, 0), 1, false, false);
        Assert.Equal((ushort)1, grace.End(1, (0, 0), action: true));
        Assert.False(grace.Protects(2, (0, 0)));
        grace.Start(100, (3, 4), 2, false, false);
        Assert.True(grace.Protects(159, (3, 4)));
        Assert.Equal((ushort)2, grace.End(160, (3, 4)));
    }

    [Fact]
    public void Native_buff_definition_is_visible_and_packet_announces_sixty_seconds()
    {
        using var definitions = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(BenchCraftingTests.Data(), "tables/BuffDefinitions.json")));
        var buff = definitions.RootElement[GracePeriod.BuffRow];
        Assert.Equal("grace_period", buff.GetProperty("Id").GetString());
        Assert.False(buff.GetProperty("Invisible").GetBoolean());
        Assert.False(buff.GetProperty("IsRecovery").GetBoolean());
        Assert.Equal(8, buff.GetProperty("BuffVisualsKey").GetInt32());
        var packet = GracePeriod.BeginPacket(1, 7);
        // Independently decode the network field as the client does, not via AddBuff's own writer.
        Assert.Equal((byte)0x02, packet[22]);
        uint durationBits = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(23, 4));
        Assert.Equal(0x42700000u, durationBits);
        Assert.Equal(60f, BitConverter.UInt32BitsToSingle(durationBits));
        Assert.NotEqual(60000u, durationBits);
        Assert.Equal(31, packet.Length);
    }
}
