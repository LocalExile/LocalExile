using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

public class GuildAsyncTradeShopProtocolTests
{
    [Fact]
    public void Guild_packets_framing_and_encoders_match_3293c_layouts()
    {
        var stream = new LoginWriter();
        stream.U16(InstanceGuilds.CreateId).WStr("Exile Guild");
        stream.U16(InstanceGuilds.InviteId).WStr("PeerAcc");
        stream.U16(InstanceGuilds.AcceptInviteId).U16(2);
        stream.U16(InstanceGuilds.DeclineInviteId).U16(3);
        stream.U16(InstanceGuilds.SetMemberRankId).WStr("PeerAcc").U8(InstanceGuilds.RankOfficer);
        stream.U16(InstanceGuilds.RemoveMemberId).WStr("PeerAcc");
        stream.U16(InstanceGuilds.SetStatusId).WStr("Mapping");
        stream.U16(InstanceGuilds.RenameId).WStr("Renamed Guild");
        stream.U16(InstanceGuilds.SetTagId).WStr("EXILE");

        var split = InstanceProtocol.SplitClientMessages(stream.ToArray(), out var partial, out var unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(9, split.Count);
        Assert.Equal("Exile Guild", InstanceGuilds.ReadText(split[0].Bytes));
        Assert.Equal("PeerAcc", InstanceGuilds.ReadText(split[1].Bytes));
        Assert.Equal((ushort)2, InstanceGuilds.ReadInviteIndex(split[2].Bytes));
        Assert.Equal((ushort)3, InstanceGuilds.ReadInviteIndex(split[3].Bytes));
        Assert.Equal(("PeerAcc", InstanceGuilds.RankOfficer), InstanceGuilds.ReadSetMemberRank(split[4].Bytes));
        Assert.Equal("PeerAcc", InstanceGuilds.ReadText(split[5].Bytes));
        Assert.Equal("Mapping", InstanceGuilds.ReadText(split[6].Bytes));
        Assert.Equal("Renamed Guild", InstanceGuilds.ReadText(split[7].Bytes));
        Assert.Equal("EXILE", InstanceGuilds.ReadText(split[8].Bytes));

        // GuildDetails wire order: Name, Tag, Status, RawU64 CreatedAt.
        byte[] info = InstanceGuilds.GuildInfo("Exile Guild", "EXILE", "Mapping", 0x0102030405060708ul,
            [new InstanceGuilds.Member("LeaderAcc", 0x1122334455667788ul, InstanceGuilds.RankLeader)]);
        var r = new LoginReader(info);
        Assert.Equal(InstanceGuilds.GuildInfoId, r.U16());
        Assert.Equal("Exile Guild", r.WStr());
        Assert.Equal("EXILE", r.WStr());
        Assert.Equal("Mapping", r.WStr());
        Assert.Equal(0x0102030405060708ul, r.RawU64());
        Assert.Equal((ushort)1, r.U16());
        Assert.Equal("LeaderAcc", r.WStr());
        Assert.Equal(0x1122334455667788ul, r.RawU64());
        Assert.Equal(InstanceGuilds.RankLeader, r.U8());

        // Invite, Expired, Left, MemberUpsert, MemberRemove, DetailsChanged, and S->C 0x00cc reorder.
        byte[] inv = InstanceGuilds.InviteNotify(4, "Exile Guild", "EXILE", "Mapping", 0x0102030405060708ul, "LeaderAcc");
        var ir = new LoginReader(inv);
        Assert.Equal(InstanceGuilds.InviteNotifyId, ir.U16());
        Assert.Equal((ushort)4, ir.U16());
        Assert.Equal("Exile Guild", ir.WStr());
        Assert.Equal("EXILE", ir.WStr());
        Assert.Equal("Mapping", ir.WStr());
        Assert.Equal(0x0102030405060708ul, ir.RawU64());
        Assert.Equal("LeaderAcc", ir.WStr());

        Assert.Equal([0x01, 0x47, 0x00, 0x04], InstanceGuilds.InviteExpired(4));
        Assert.Equal([0x01, 0x49], InstanceGuilds.GuildLeft());
        Assert.Equal(InstanceGuilds.DetailsChangedId, BinaryPrimitives.ReadUInt16BigEndian(
            InstanceGuilds.DetailsChanged("Exile Guild", "EXILE", "Mapping", 0x0102030405060708ul)));
        Assert.Equal(InstanceGuilds.MemberUpsertId, BinaryPrimitives.ReadUInt16BigEndian(
            InstanceGuilds.MemberUpsert("PeerAcc", 123, InstanceGuilds.RankOfficer)));
        Assert.Equal(InstanceGuilds.MemberRemoveId, BinaryPrimitives.ReadUInt16BigEndian(InstanceGuilds.MemberRemove("PeerAcc")));

        byte[] reorder = InstanceStash.EncodeReorderTabs(InstanceStash.Guild, [(1, 0), (0, 1)]);
        Assert.Equal([0x00, 0xcc, 0x01, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01], reorder);
    }

    [Fact]
    public void Async_trade_packets_match_3293c_wire_layouts()
    {
        // S->C 0x02ad full snapshot trailing byte must be 0; incremental must be 1.
        byte[] itemWire = [0xaa, 0xbb, 0xcc];
        byte[] snapshot = InstanceAsyncTrade.EncodeHistory(
            [new(0x0102030405060708ul, 0x11223344u, 25u, true, itemWire)], isIncremental: false);
        Assert.Equal(InstanceAsyncTrade.HistoryId, BinaryPrimitives.ReadUInt16BigEndian(snapshot));
        Assert.Equal((byte)1, snapshot[2]); // CompactUInt count = 1
        Assert.Equal(0x0102030405060708ul, BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(3)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(snapshot.AsSpan(11)));
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32BigEndian(snapshot.AsSpan(15)));
        Assert.Equal((byte)1, snapshot[19]);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16BigEndian(snapshot.AsSpan(20)));
        Assert.Equal(itemWire, snapshot[22..25]);
        Assert.Equal((byte)0, snapshot[^1]);

        byte[] incremental = InstanceAsyncTrade.EncodeHistory([], isIncremental: true);
        Assert.Equal([0x02, 0xad, 0x00, 0x01], incremental);

        // S->C 0x02ae earnings pairs.
        byte[] earnings = InstanceAsyncTrade.EncodeEarnings([new(0xdeadbeefu, 42u)]);
        Assert.Equal([0x02, 0xae, 0x01, 0xde, 0xad, 0xbe, 0xef, 0x00, 0x00, 0x00, 0x2a], earnings);

        // C->S 0x02ac & 0x02b0 framing and parsing.
        var stream = new LoginWriter()
            .U16(InstanceAsyncTrade.RequestHistoryId)
            .U16(InstanceAsyncTrade.TakeEarningsId).U32(0x12345678u).U32(10u).U8(1).U8(2)
            .ToArray();
        var split = InstanceProtocol.SplitClientMessages(stream, out var partial, out var unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(2, split.Count);
        var take = InstanceAsyncTrade.ReadTakeEarnings(split[1].Bytes);
        Assert.Equal(new InstanceAsyncTrade.TakeEarningsRequest(0x12345678u, 10u, 1, 2), take);

        // S->C 0x008e: rawItemClass MUST be little-endian (no byte swap at 0x141fa8f6f..0x141fa8f80).
        byte[] highlight = InstanceAsyncTrade.EncodeTargetHighlight(0x0007, 0x0a00, 0x60000001, 0x11223344, [(0x55667788u, 3u)]);
        Assert.Equal(InstanceAsyncTrade.TargetHighlightId, BinaryPrimitives.ReadUInt16BigEndian(highlight));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16BigEndian(highlight.AsSpan(2)));
        Assert.Equal(0x0a00u, BinaryPrimitives.ReadUInt32BigEndian(highlight.AsSpan(4)));
        Assert.Equal(0x60000001u, BinaryPrimitives.ReadUInt32BigEndian(highlight.AsSpan(8)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(highlight.AsSpan(12)));
        Assert.Equal((byte)1, highlight[16]);
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32BigEndian(highlight.AsSpan(17)));
        Assert.Equal((byte)3, highlight[21]);
    }

    [Fact]
    public void Shop_lz4_packages_errors_and_box_packets_roundtrip()
    {
        // S->C 0x0132 empty JSON produces zero-length blob.
        Assert.Equal([0x01, 0x32, 0x00, 0x00, 0x00, 0x00], InstanceShop.EncodePaymentPackagesResult(0, ""));

        // S->C 0x0132 non-empty JSON wraps u32 LE innerLen + LZ4 raw block over (u32 LE utf8Len + UTF-8 JSON).
        string json = "{\"packages\":[{\"id\":\"pack_a\",\"price\":0},{\"id\":\"pack_b\",\"price\":0},{\"id\":\"pack_c\",\"price\":0}]}";
        byte[] pkt = InstanceShop.EncodePaymentPackagesResult(0, json);
        Assert.Equal(InstanceShop.PaymentPackagesResultId, BinaryPrimitives.ReadUInt16BigEndian(pkt));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(2)));
        ushort blobLen = BinaryPrimitives.ReadUInt16BigEndian(pkt.AsSpan(4));
        Assert.Equal(pkt.Length - 6, blobLen);
        int uncompressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(6));
        byte[] inner = InstanceShop.DecompressLz4RawBlock(pkt.AsSpan(10), uncompressedSize);
        int strLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(inner);
        Assert.Equal(json, Encoding.UTF8.GetString(inner, 4, strLen));

        // S->C 0x0282 JSON error and S->C 0x0133 shipping quote.
        byte[] err = InstanceShop.EncodeJsonErrorMessage("Offline only", ["package"]);
        Assert.Equal(InstanceShop.JsonErrorId, BinaryPrimitives.ReadUInt16BigEndian(err));
        ushort errLen = BinaryPrimitives.ReadUInt16BigEndian(err.AsSpan(2));
        using (var doc = JsonDocument.Parse(Encoding.UTF8.GetString(err, 4, errLen)))
            Assert.Equal("Offline only", doc.RootElement.GetProperty("error").GetProperty("message").GetString());

        byte[] quote = InstanceShop.EncodeShippingQuote(0, "{\"cost\":0}");
        Assert.Equal(InstanceShop.ShippingQuoteId, BinaryPrimitives.ReadUInt16BigEndian(quote));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(quote.AsSpan(2)));
        Assert.Equal("{\"cost\":0}", Encoding.UTF8.GetString(quote, 6, BinaryPrimitives.ReadUInt16BigEndian(quote.AsSpan(4))));

        // C->S 0x012c, 0x012e, 0x0130, 0x0138, 0x0206, 0x026e framing and readers.
        var stream = new LoginWriter();
        stream.U16(InstanceShop.BuyId).WStr("MysteryPackChaosVsOrderBox").WStr("").U32(1).U8(0);
        stream.U16(InstanceShop.ReloadId);
        stream.U16(InstanceShop.ShippingQuoteRequestId).U16(2).Bytes("US"u8).U16(0).U16(0).U16(0);
        stream.U16(InstanceShop.PlaceOrderId).U16(1).Bytes("a"u8).U16(1).Bytes("b"u8).U16(1).Bytes("c"u8).U16(1).Bytes("d"u8);
        stream.U16(InstanceShop.OpenMysteryBoxId).U32(0x00100001);
        stream.U16(InstanceShop.OpenSalvageBoxId).U32(0x00100002).U16(2).U32(101).U32(102);

        var split = InstanceProtocol.SplitClientMessages(stream.ToArray(), out var partial, out var unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(6, split.Count);
        Assert.Equal("MysteryPackChaosVsOrderBox", InstanceShop.ReadBuy(split[0].Bytes).PackageId);
        Assert.Equal("US", InstanceShop.ReadFourStrings(split[2].Bytes).Field0);
        Assert.Equal("d", InstanceShop.ReadFourStrings(split[3].Bytes).Field3);
        Assert.Equal(0x00100001u, InstanceShop.ReadOpenMysteryBox(split[4].Bytes));
        var salvage = InstanceShop.ReadOpenSalvageBox(split[5].Bytes);
        Assert.Equal(0x00100002u, salvage.BoxRecordId);
        Assert.Equal(new uint[] { 101u, 102u }, salvage.ConsumedRecordIds);

        // S->C 0x0204 / 0x0205 incremental MTX record upsert/remove.
        byte[] upsert = InstanceMicrotransactions.RecordUpsert(new(0x11223344u, 0x100005u), justReceived: true);
        Assert.Equal(InstanceMicrotransactions.RecordUpsertId, BinaryPrimitives.ReadUInt16BigEndian(upsert));
        Assert.Equal((byte)1, upsert[^1]);
        byte[] remove = InstanceMicrotransactions.RecordRemove(0x100005u);
        Assert.Equal([0x02, 0x05, 0x00, 0x00, 0x00, 0x10, 0x00, 0x05], remove);

        // Truncated spans throw InvalidDataException.
        Assert.Throws<InvalidDataException>(() => InstanceShop.ReadOpenSalvageBox([0x02, 0x6e, 0, 0, 0, 1, 0, 2, 0, 0, 0, 9]));
        Assert.Throws<InvalidDataException>(() => InstanceShop.ReadFourStrings([0x01, 0x30, 0x00, 0x05, 0x41]));
    }
}
