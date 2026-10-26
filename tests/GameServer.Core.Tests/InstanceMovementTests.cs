using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

public class InstanceMovementTests
{
    private static string Compact(int v) { var w = new LoginWriter(); InstanceProtocol.CompactInt(w, v); return Convert.ToHexString(w.ToArray()); }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(63, "3F")]
    [InlineData(-1, "7F")]
    [InlineData(-64, "40")]
    [InlineData(64, "8040")]
    [InlineData(222, "80DE")]
    [InlineData(0x1fff, "9FFF")]
    [InlineData(-65, "BFBF")]
    [InlineData(0x2000, "C02000")]
    [InlineData(0x100000, "E0100000")]
    [InlineData(0x4000000, "F004000000")]
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
        Assert.Equal("01B700001000000000000000000000E8000000E50000000000000000",
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
    [InlineData(0x0192, true)]
    [InlineData(0x0195, true)]
    [InlineData(0x0025, false)]   // predictive move (UseSkillAtLocation)
    [InlineData(0x001b, false)]   // tick
    public void Only_the_three_lockstep_messages_count_as_lockstep_movement(ushort id, bool lockstep) =>
        Assert.Equal(lockstep, InstanceProtocol.IsLockstepMovement(id));

    [Fact]
    public void Splitter_frames_known_messages_and_keeps_partials()
    {
        // Writer-proven 4-byte 0x0099, a complete tick, then a tick cut in two.
        byte[] chunk = Convert.FromHexString("00990100" + "001B0005768AF5" + "001B00");
        var msgs = InstanceProtocol.SplitClientMessages(chunk, out byte[] partial, out byte[] unknown);
        Assert.Equal(new ushort[] { 0x0099, 0x001b }, msgs.Select(m => m.Id));
        Assert.Equal("001B00", Convert.ToHexString(partial));
        Assert.Empty(unknown);

        InstanceProtocol.SplitClientMessages(Convert.FromHexString("ABCD01"), out _, out unknown);
        Assert.Equal("ABCD01", Convert.ToHexString(unknown));
    }
}
