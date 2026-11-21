using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

public class InstanceC2sFramingTests
{
    static readonly byte[] Tick = Convert.FromHexString("001B00000000AA");

    [Fact]
    public void Previously_unframed_handled_opcodes_frame_cleanly_when_coalesced_with_ticks()
    {
        Assert.Equal(2, InstanceProtocol.KnownClientLength(InstanceDelve.ChartRequestId));
        Assert.Equal(InstanceMonsterStatue.CreateLength, InstanceProtocol.KnownClientLength(InstanceMonsterStatue.CreateId));
        Assert.Equal(InstanceItems.CurrencyApplicationForesightRequestLength,
            InstanceProtocol.KnownClientLength(InstanceItems.CurrencyApplicationForesightRequestId));

        byte[] chartReq = Convert.FromHexString("023F");
        byte[] statueCreate = [0x02, 0x09, 0x36, 0x5B, 0xD6, 0x38, 0x00, 0x00, 0x30, 0x40];
        byte[] foresightReq = new LoginWriter()
            .U16(InstanceItems.CurrencyApplicationForesightRequestId)
            .U16(0x002a).U32(1).U32(10).U32(1).U32(20).ToArray();

        byte[] stream = [.. chartReq, .. Tick, .. statueCreate, .. Tick, .. foresightReq, .. Tick];
        var framed = InstanceProtocol.SplitClientMessages(stream, out var partial, out var unknown);

        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(new ushort[]
        {
            InstanceDelve.ChartRequestId, InstanceProtocol.TickId,
            InstanceMonsterStatue.CreateId, InstanceProtocol.TickId,
            InstanceItems.CurrencyApplicationForesightRequestId, InstanceProtocol.TickId,
        }, framed.Select(m => m.Id));

        var (action, target) = InstanceMonsterStatue.ReadCreate(framed[2].Bytes);
        Assert.Equal(InstanceMonsterStatue.CreatorAction, action);
        Assert.Equal(0x3040u, target);

        var parsed = InstanceItems.ReadCurrencyApplicationForesightRequest(framed[4].Bytes);
        Assert.Equal(new InstanceItems.CurrencyApplicationForesightRequest(0x002a, 1, 10, 1, 20), parsed);
    }

    [Theory]
    [InlineData("015801", 3)]
    [InlineData("01580F", 3)]
    [InlineData("01580011223344", 7)]
    [InlineData("01580E11223344", 7)]
    public void Opcode_0158_frames_both_3_byte_and_7_byte_forms_across_all_splits(string hex, int expectedLength)
    {
        byte[] packet = Convert.FromHexString(hex);
        Assert.Equal(expectedLength, packet.Length);
        AssertFramesAcrossAllSplits(0x0158, packet);
    }

    [Theory]
    [InlineData(0x0081, "general", "en")]
    [InlineData(0x0280, "global", "Русский")]
    [InlineData(0x0280, "中文频道", "Exile😀")]
    public void Two_wstr_opcodes_0081_and_0280_frame_across_all_splits(ushort id, string first, string second)
    {
        byte[] packet = new LoginWriter().U16(id).WStr(first).WStr(second).ToArray();
        AssertFramesAcrossAllSplits(id, packet);
    }

    [Theory]
    [InlineData(0x0097)]
    [InlineData(0x00ca)]
    [InlineData(0x0140)]
    [InlineData(0x0141)]
    [InlineData(0x0145)]
    [InlineData(0x014d)]
    [InlineData(0x014e)]
    [InlineData(0x014f)]
    [InlineData(0x0155)]
    public void Single_wstr_opcodes_frame_across_all_splits(ushort id)
    {
        byte[] packet = new LoginWriter().U16(id).WStr("Exile_Имя_流放😀").ToArray();
        AssertFramesAcrossAllSplits(id, packet);
    }

    [Theory]
    [InlineData(0x0098)]
    [InlineData(0x0144)]
    [InlineData(0x0179)]
    public void Wstr_plus_u8_opcodes_frame_across_all_splits(ushort id)
    {
        byte[] packet = new LoginWriter().U16(id).WStr("Target_Имя").U8(0x05).ToArray();
        AssertFramesAcrossAllSplits(id, packet);
    }

    [Fact]
    public void Atlas_tree_rename_02a7_frames_across_all_splits()
    {
        byte[] packet = new LoginWriter().U16(0x02a7).U8(2).WStr("Bossing_Дерево").ToArray();
        AssertFramesAcrossAllSplits(0x02a7, packet);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(200)]   // 2-byte CompactUInt (0x80..)
    [InlineData(17000)] // 3-byte CompactUInt (0xc0..)
    public void Ruleset_message_02ca_frames_for_compact_uint_payload_lengths(int payloadLen)
    {
        var w = new LoginWriter().U16(0x02ca).U8(3);
        InstanceProtocol.CompactUInt(w, (uint)payloadLen);
        byte[] payload = new byte[payloadLen];
        for (int i = 0; i < payloadLen; i++) payload[i] = (byte)(i & 0xff);
        w.Bytes(payload);
        byte[] packet = w.ToArray();

        if (payloadLen <= 200)
        {
            AssertFramesAcrossAllSplits(0x02ca, packet);
        }
        else
        {
            var framed = InstanceProtocol.SplitClientMessages([.. Tick, .. packet, .. Tick], out var partial, out var unknown);
            Assert.Empty(partial);
            Assert.Empty(unknown);
            Assert.Equal(new ushort[] { InstanceProtocol.TickId, 0x02ca, InstanceProtocol.TickId }, framed.Select(m => m.Id));
            Assert.Equal(packet, framed[1].Bytes);
        }
    }

    [Fact]
    public void Ruleset_message_02ca_rejects_oversized_payload_length_as_unknown_without_throwing()
    {
        // CompactUInt 0x10000 (3-byte: c1 00 00) exceeds client reader's 0xffff limit.
        byte[] bad = [0x02, 0xca, 0x01, 0xc1, 0x00, 0x00];
        var framed = InstanceProtocol.SplitClientMessages(bad, out var partial, out var unknown);
        Assert.Empty(framed);
        Assert.Empty(partial);
        Assert.Equal(bad, unknown);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    public void Recombine_028f_frames_across_all_splits(int n1, int n2)
    {
        var w = new LoginWriter().U16(0x028f).U8(0).U8((byte)n1);
        for (int i = 0; i < n1; i++) w.U8((byte)(0x10 + i));
        w.U8((byte)n2);
        for (int i = 0; i < n2; i++) w.U8((byte)(0x20 + i));
        byte[] packet = w.U8(1).U8(0).ToArray();
        AssertFramesAcrossAllSplits(0x028f, packet);
    }

    [Fact]
    public void Shop_buy_012c_frames_across_all_splits()
    {
        byte[] packet = new LoginWriter().U16(0x012c).WStr("PackName").WStr("Купон").U32(42).U8(1).ToArray();
        AssertFramesAcrossAllSplits(0x012c, packet);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Salvage_box_026e_frames_across_all_splits(int count)
    {
        var w = new LoginWriter().U16(0x026e).U32(0x11223344).U16((ushort)count);
        for (int i = 0; i < count; i++) w.U32((uint)(0x500 + i));
        AssertFramesAcrossAllSplits(0x026e, w.ToArray());
    }

    [Fact]
    public void Instance_client_string_02fc_and_two_blob_0281_frame_across_all_splits()
    {
        byte[] p2fc = new LoginWriter().U16(0x02fc).Str("client-string").ToArray();
        AssertFramesAcrossAllSplits(0x02fc, p2fc);

        byte[] p281 = new LoginWriter().U16(0x0281).Blob([1, 2, 3]).Blob([4, 5, 6, 7]).ToArray();
        AssertFramesAcrossAllSplits(0x0281, p281);
    }

    [Theory]
    [InlineData(0x017a, "ExileName")]
    [InlineData(0x017b, "ExileName")]
    [InlineData(0x017a, "Изгнанник")]
    [InlineData(0x017b, "流放者")]
    [InlineData(0x017a, "Exile😀")]
    public void Visit_hideout_and_menagerie_handle_ascii_and_non_ascii_compact_wstr(ushort id, string characterName)
    {
        byte[] packet = new LoginWriter().U16(id).WStr(characterName).U32(0x2f00).U8(0).ToArray();
        Assert.Equal(packet.Length, InstanceWaypoints.VisitHideoutLength(packet));
        AssertFramesAcrossAllSplits(id, packet);

        var parsed = InstanceWaypoints.ReadVisitHideout(packet);
        Assert.Equal(new InstanceWaypoints.VisitHideout(characterName, 0x2f00, 0), parsed);
    }

    [Theory]
    [InlineData(0x0038, 2)]
    [InlineData(0x001a, 3)]
    [InlineData(0x011d, 4)]
    [InlineData(0x0249, 5)]
    [InlineData(0x004b, 6)]
    [InlineData(0x002e, 7)]
    [InlineData(0x0062, 8)]
    [InlineData(0x00e2, 9)]
    [InlineData(0x0061, 10)]
    [InlineData(0x0057, 11)]
    [InlineData(0x005f, 12)]
    [InlineData(0x002c, 13)]
    [InlineData(0x0067, 14)]
    [InlineData(0x005a, 16)]
    [InlineData(0x0076, 17)]
    [InlineData(0x0079, 18)]
    [InlineData(0x0089, 20)]
    [InlineData(0x002b, 21)]
    [InlineData(0x029f, 25)]
    [InlineData(0x0073, 26)]
    [InlineData(0x029e, 28)]
    public void Sampled_exact_fixed_length_opcodes_match_c2s_json_lengths(ushort id, int expectedLength)
    {
        Assert.Equal(expectedLength, InstanceProtocol.KnownClientLength(id));
        byte[] packet = new byte[expectedLength];
        packet[0] = (byte)(id >> 8);
        packet[1] = (byte)id;
        AssertFramesAcrossAllSplits(id, packet);
    }

    [Theory]
    [InlineData(0x000e)]
    [InlineData(0x000f)]
    [InlineData(0xf7e0)]
    [InlineData(0xabcd)]
    public void Plaintext_opening_and_unregistered_opcodes_stay_out_of_KnownClientLength(ushort id)
    {
        Assert.Null(InstanceProtocol.KnownClientLength(id));
    }

    [Theory]
    [InlineData("0015000181")]                  // 0x0015 chat: invalid tag 0x81
    [InlineData("001500018000D8")]              // 0x0015 chat: surrogate inside 0x80
    [InlineData("00150001C041000000")]          // 0x0015 chat: BMP code point inside 0xC0
    [InlineData("0016010001FF")]                // 0x0016 command: invalid tag 0xFF
    [InlineData("00800001810000")]              // 0x0080 friend add: invalid tag
    [InlineData("0093000181")]                  // 0x0093 party invite: invalid tag
    [InlineData("00CD0000010000000000000000000181")] // 0x00cd rename tab: invalid tag
    [InlineData("017A00018100002F0000")]        // 0x017a visit hideout: invalid tag
    [InlineData("02800000000181")]              // 0x0280 second wstr: invalid tag
    [InlineData("02A700000181")]                // 0x02a7 atlas tree rename: invalid tag
    [InlineData("012C00000001810000000000")]    // 0x012c shop buy: invalid tag in second wstr
    public void Malformed_wstr_bytes_in_SplitClientMessages_never_throw_and_return_unknown(string hex)
    {
        byte[] bad = Convert.FromHexString(hex);
        var framed = InstanceProtocol.SplitClientMessages([.. Tick, .. bad], out var partial, out var unknown);

        var only = Assert.Single(framed);
        Assert.Equal(InstanceProtocol.TickId, only.Id);
        Assert.Empty(partial);
        Assert.Equal(bad, unknown);
    }

    static void AssertFramesAcrossAllSplits(ushort id, byte[] packet)
    {
        byte[] stream = [.. Tick, .. packet, .. Tick];
        for (int split = 0; split <= stream.Length; split++)
        {
            var framed = InstanceProtocol.SplitClientMessages(stream.AsSpan(0, split), out var carry, out var unknown);
            Assert.Empty(unknown);
            framed.AddRange(InstanceProtocol.SplitClientMessages([.. carry, .. stream[split..]], out carry, out unknown));
            Assert.Empty(carry);
            Assert.Empty(unknown);
            Assert.Equal(new ushort[] { InstanceProtocol.TickId, id, InstanceProtocol.TickId }, framed.Select(m => m.Id));
            Assert.Equal(packet, framed[1].Bytes);
        }
    }
}
