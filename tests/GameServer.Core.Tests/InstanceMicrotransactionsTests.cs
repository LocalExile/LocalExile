using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceMicrotransactionsTests
{
    [Fact]
    public void One_record_is_the_bytes_the_client_reads()
    {
        // 22-microtransactions.md §4: u8 +0x08, u32 +0x04 (the MtxTypes HASH32 the panel resolves), u32 +0x00 (the
        // record id), u64 raw +0x10, two wstrs, u8 +0x09, the two variant flags (0), then the three empty lists and the
        // flags byte. Live 2026-10-22 the client read exactly these bytes in this order (trace-mtx-read.js).
        byte[] m = InstanceMicrotransactions.Purchased(
        [
            new InstanceMicrotransactions.Record(MtxTypeHash: 0x12345678, Id: 0x00ABCDEF, Name: "Hi",
                Qword: 0x1122334455667788),
        ]);
        Assert.Equal("01F3" + "01" + "00" + "12345678" + "00ABCDEF" + "8877665544332211" + "00024869" + "0000" +
            "00" + "00" + "00" + "000000" + "01", Convert.ToHexString(m));
    }

    [Fact]
    public void The_add_message_counts_in_four_bytes_and_ends_in_one_flag()
    {
        // 22-microtransactions.md §3/§8: 0x1f4 is the same record list with a u32 count, and one flags byte after it
        // (bit 0 = which list). Live 2026-10-22 this is the message that makes the panel rebuild its rows.
        byte[] one = InstanceMicrotransactions.Added(
        [
            new InstanceMicrotransactions.Record(MtxTypeHash: 0x12345678, Id: 0x00ABCDEF, Name: "Hi",
                Qword: 0x1122334455667788),
        ]);
        Assert.Equal("01F4" + "00000001" + "00" + "12345678" + "00ABCDEF" + "8877665544332211" + "00024869" +
            "0000" + "00" + "00" + "00" + "00", Convert.ToHexString(one));
        // An empty one is "no records": live, a count-0 message replaced a filled list with nothing and rebuilt the
        // panel (which is why the gateway never sends it on its own).
        Assert.Equal("01F4" + "00000000" + "00", Convert.ToHexString(InstanceMicrotransactions.Added([])));
    }

    [Fact]
    public void The_flags_byte_picks_the_list_the_store_reads()
    {
        // 22-microtransactions.md §1/§8: bit 0 of the flags byte selects the panel's records (0) or the store's owned
        // list (1) — live 2026-10-22 this exact message put ALREADY OWNED on the shop's tile for the record's hash.
        // Bits 0 and 1 together (3) fill the store's second list, which no visible page has shown.
        byte[] store = InstanceMicrotransactions.Added(
            [new InstanceMicrotransactions.Record(MtxTypeHash: 0xB63B39AE, Id: 269)], InstanceMicrotransactions.StoreOwned);
        Assert.Equal("01F4" + "00000001" + "00" + "B63B39AE" + "0000010D" + "0000000000000000" + "0000" + "0000" +
            "00" + "00" + "00" + "01", Convert.ToHexString(store));
        Assert.Equal((byte)0, InstanceMicrotransactions.Records);
        Assert.Equal((byte)3, InstanceMicrotransactions.StoreSecond);
        // The guild's list, which the gateway sends empty: count 0 and the flags byte 3 (live 2026-10-22 the client
        // took this exact message and the shop's guild pages then marked nothing).
        Assert.Equal("01F4" + "00000000" + "03",
            Convert.ToHexString(InstanceMicrotransactions.Added([], InstanceMicrotransactions.StoreSecond)));
    }

    [Fact]
    public void An_empty_list_is_the_id_and_four_bytes()
    {
        Assert.Equal("01F3" + "00" + "000000" + "01", Convert.ToHexString(InstanceMicrotransactions.Purchased([])));
    }

    [Fact]
    public void The_list_reads_back()
    {
        List<InstanceMicrotransactions.Record> records =
        [
            new(MtxTypeHash: 0x0DF0_0001, Id: 278, "Black Hole Portal Effect", "A portal effect", 0x0102030405060708, 3, 4),
            new(234032333, 63, "Ébène ✨", ""),
        ];
        var read = InstanceMicrotransactions.ReadPurchased(InstanceMicrotransactions.Purchased(records));
        Assert.Equal(records, read.Records);
        Assert.Equal((byte)0, read.CountA);
        Assert.Equal(InstanceMicrotransactions.DefaultFlags, read.Flags);
    }

    [Fact]
    public void More_records_than_the_byte_count_holds_are_rejected()
    {
        var records = Enumerable.Range(0, 256).Select(i => new InstanceMicrotransactions.Record((uint)i, 0)).ToList();
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceMicrotransactions.Purchased(records));
    }

    [Fact]
    public void A_slot_pick_is_the_slot_row_and_the_selection()
    {
        // 22-microtransactions.md §9: the COSMETICS tab's flyout sends 8 bytes, no length prefix — the u16 id, the
        // MicrotransactionSlot row Id (20 = Portal) and the pick. This is the exact message a click on the Arctic portal
        // sent live 2026-10-22: 01 F9 14 00 00 00 0B 00 (11 = "Arctic Portal Effect"'s MicrotransactionPortalVariations
        // Index, the same number the catalogue keys by).
        var pick = InstanceMicrotransactions.ReadSlot(Convert.FromHexString("01F9140000000B00"));
        Assert.Equal(InstanceMicrotransactions.PortalSlot, pick.SlotRow);
        Assert.Equal((ushort)11, pick.Selection);

        // The client frames it as a fixed 8-byte message, or the read loop stops at it as unknown and loses the rest.
        Assert.Equal(8, InstanceProtocol.KnownClientLength(InstanceMicrotransactions.SlotId));
        Assert.Equal(InstanceMicrotransactions.SlotLength, InstanceProtocol.KnownClientLength(InstanceMicrotransactions.SlotId));
        var framed = InstanceProtocol.SplitClientMessages(Convert.FromHexString("01F9140000000B00"), out _, out byte[] unknown);
        Assert.Empty(unknown);
        var single = Assert.Single(framed);
        Assert.Equal(InstanceMicrotransactions.SlotId, single.Id);
        Assert.Equal("01F9140000000B00", Convert.ToHexString(single.Bytes));
    }

    [Fact]
    public void The_slot_reader_rejects_anything_else()
    {
        Assert.Throws<InvalidDataException>(() =>
            InstanceMicrotransactions.ReadSlot(Convert.FromHexString("01F4" + "14000000" + "0B00")));
        Assert.Throws<InvalidDataException>(() => InstanceMicrotransactions.ReadSlot(Convert.FromHexString("01F914000000")));
    }

    [Fact]
    public void The_reader_rejects_a_message_that_is_not_0x1f3()
    {
        Assert.Throws<InvalidDataException>(() =>
            InstanceMicrotransactions.ReadPurchased(Convert.FromHexString("01F4" + "00" + "000000" + "01")));
    }
}
