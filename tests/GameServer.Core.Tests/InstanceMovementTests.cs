using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

public class InstanceMovementTests
{
    private static string Compact(int v) { var w = new LoginWriter(); InstanceProtocol.CompactInt(w, v); return Convert.ToHexString(w.ToArray()); }

    [Theory]
    [InlineData(0, "00")]
    public void Compact_int_matches_the_client_writer(int value, string hex) => Assert.Equal(hex, Compact(value));

    [Fact]
    public void Action_started_matches_the_bytes_the_live_client_accepted()
    {
        // 2026-10-16 bytes (first pair 222, 236; second 197, 261); the encoding is unchanged, only the meaning of the pairs.
        byte[] b = InstanceProtocol.ActionStarted(0x1000, 222, 236, 197, 261, 0x2909, 0x1001, 0x0408);
        Assert.Equal("01AB00001000000000000000040880DE80EC80C5810529091001", Convert.ToHexString(b));
    }

    [Fact]
    public void Use_skill_at_location_parses_a_live_capture()
    {
        var m = InstanceProtocol.ReadUseSkillAtLocation(Convert.FromHexString("0025000000D7000000F5290910010408"));
        Assert.Equal(new InstanceProtocol.UseSkillAtLocation(0xD7, 0xF5, 0x2909, 0x1001, 0x0408), m);
    }

    [Fact]
    public void Location_instant_skill_preserves_unsigned_coordinates_and_has_no_sequence()
    {
        byte[] bytes = Convert.FromHexString("01900000011A0000012B0103");
        Assert.Equal(12, InstanceProtocol.KnownClientLength(InstanceProtocol.UseInstantSkillAtLocationId));
        Assert.Equal((282u, 299u, (ushort)0x0103), InstanceProtocol.ReadUseInstantSkillAtLocation(bytes));
        Assert.Equal((uint.MaxValue, 0x80000000u, (ushort)0x0103),
            InstanceProtocol.ReadUseInstantSkillAtLocation(Convert.FromHexString("0190FFFFFFFF800000000103")));
    }

    [Theory]
    [InlineData(1)]
    public void Split_location_instant_request_keeps_following_object_instant_and_tick(int cut)
    {
        byte[] location = Convert.FromHexString("01900000011A0000012B0103");
        Assert.Empty(InstanceProtocol.SplitClientMessages(location[..cut], out var partial, out var unknown));
        Assert.Equal(location[..cut], partial);
        Assert.Empty(unknown);
        byte[] remainder = [.. partial, .. location[cut..], .. Convert.FromHexString("0191000010000105001B00")];
        var messages = InstanceProtocol.SplitClientMessages(remainder, out partial, out unknown);
        Assert.Equal(new ushort[] { 0x190, 0x191 }, messages.Select(m => m.Id));
        Assert.Equal(location, messages[0].Bytes);
        Assert.Equal("001B00", Convert.ToHexString(partial));
        Assert.Empty(unknown);
    }

    [Fact]
    public void Location_instant_acceptance_has_a_ground_target_and_no_action_sequence()
    {
        Assert.Equal("01CE000010000000000000000103FFFF000080FE8138",
            Convert.ToHexString(InstanceProtocol.InstantAction(0x1000, 0x0103, 254, 312)));
    }

    [Fact]
    public void Use_instant_skill_parses_a_live_capture()
    {
        // Haste (skill 0x0105) cast on the player itself.
        var bytes = Convert.FromHexString("0191000010000105");

        Assert.Equal(bytes.Length, InstanceProtocol.KnownClientLength(InstanceProtocol.UseInstantSkillId));
        Assert.Equal((0x1000u, (ushort)0x0105), InstanceProtocol.ReadUseInstantSkill(bytes));
    }

    [Fact]
    public void Instant_action_answers_with_the_skill_no_sequence_and_the_target_object()
    {
        string hex = Convert.ToHexString(InstanceProtocol.InstantAction(0x1000, 0x0105, 254, 312, targetObjectId: 0x1000));

        // object header, skill 0x0105, sequence 0xffff (none), flags 0x20 (+ u32 target object), then the position
        Assert.StartsWith("01CE" + "00001000" + "00000000" + "0000" + "0105" + "FFFF" + "0020" + "00001000", hex);
    }

    [Fact]
    public void Wake_zone_pong_move_and_teleport_layouts()
    {
        Assert.Equal("01A900001000000000000000", Convert.ToHexString(InstanceProtocol.EnterWakeZone(0x1000)));
        Assert.Equal("001C0000000012345678", Convert.ToHexString(InstanceProtocol.Pong(0, 0, 0x12345678)));
        Assert.Equal("01BA00001000000000000000000000E8000000E5000000004214000000",
            Convert.ToHexString(InstanceProtocol.MoveTo(0x1000, 232, 229, 37f)));
        Assert.Equal("01B700001000000000000000000000E8000000E57FFFFFFF00000000",
            Convert.ToHexString(InstanceProtocol.Teleport(0x1000, 232, 229)));
    }

    [Fact]
    public void Lockstep_movement_messages_are_framed()
    {
        // Captured live 2026-10-18 with Network Mode "auto" (lockstep on localhost): one click sent start, update, stop.
        // Each is followed here by a tick, which must still be framed.
        byte[] chunk = Convert.FromHexString("018E00000029FFFFFFEF2909040200" + "001B0046997F00" + "019200000010FFFFFFF2" +
            "001B0046997F00" + "0195" + "001B0046997F00");
        var msgs = InstanceProtocol.SplitClientMessages(chunk, out byte[] partial, out byte[] unknown);
        Assert.Equal(new ushort[] { 0x018e, 0x001b, 0x0192, 0x001b, 0x0195, 0x001b }, msgs.Select(m => m.Id));
        Assert.Equal(new[] { 15, 7, 10, 7, 2, 7 }, msgs.Select(m => m.Bytes.Length));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData(0x018e, true)]
    public void Only_the_three_lockstep_messages_count_as_lockstep_movement(ushort id, bool lockstep) =>
        Assert.Equal(lockstep, InstanceProtocol.IsLockstepMovement(id));

    [Fact]
    public void Splitter_frames_known_messages_and_keeps_partials()
    {
        // 0x0099, the two chat channel joins and 0x0129, then a tick, as one live chunk; then a tick cut in two.
        byte[] chunk = Convert.FromHexString("00990000" + "01340334000100" + "01340001010100" + "0129" + "001B0005768AF5" + "001B00");
        var msgs = InstanceProtocol.SplitClientMessages(chunk, out byte[] partial, out byte[] unknown);
        Assert.Equal(new ushort[] { 0x0099, 0x0134, 0x0134, 0x0129, 0x001b }, msgs.Select(m => m.Id));
        Assert.Equal("001B00", Convert.ToHexString(partial));
        Assert.Empty(unknown);

        // A join cut across two reads waits for its last bytes.
        msgs = InstanceProtocol.SplitClientMessages(Convert.FromHexString("0129" + "01340334"), out partial, out unknown);
        Assert.Equal(new ushort[] { 0x0129 }, msgs.Select(m => m.Id));
        Assert.Equal("01340334", Convert.ToHexString(partial));
        Assert.Empty(unknown);

        InstanceProtocol.SplitClientMessages(Convert.FromHexString("ABCD01"), out _, out unknown);
        Assert.Equal("ABCD01", Convert.ToHexString(unknown));
    }
}
