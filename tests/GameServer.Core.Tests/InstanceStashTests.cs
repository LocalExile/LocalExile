using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceStashTests
{
    // The read order of 0x141d50880: u32 inventory, u32, wstr name, str id, u16 position, flags, u16 parent, R G B, type.
    [Fact]
    public void Tab_record_follows_the_client_reader()
    {
        var tab = new InstanceStash.Tab(Index: 2, Name: "Ab", Inventory: 0x802, Type: 7, R: 1, G: 2, B: 3,
            Flags: InstanceStash.TabFlags.Premium, Position: 2, Id: "x");
        string hex = Convert.ToHexString(InstanceStash.WriteTab(new(), tab).ToArray());
        Assert.Equal("00000802" + "00000000" + "0002" + "4162" + "0001" + "78" + "0002" + "02" + "00" + "FFFF" + "010203" + "07", hex);
    }

    [Fact]
    public void Affinities_and_loaded_set_their_flags_and_bytes()
    {
        var tab = new InstanceStash.Tab(0, "", 0x800, 0, 0, 0, 0, Affinities: 1, Loaded: true);
        string hex = Convert.ToHexString(InstanceStash.WriteTab(new(), tab).ToArray());
        // flags 0x40 (affinities follow) and 0x02 in the second byte (loaded); the bitset raw (little-endian).
        Assert.EndsWith("0000" + "40" + "02" + "FFFF" + "000000" + "00" + "01000000", hex);
    }

    [Fact]
    public void Enter_area_carries_the_stash_tabs_first()
    {
        var tab = new InstanceStash.Tab(0, "", 0x800, 0, 0, 0, 0);
        string tabHex = Convert.ToHexString(InstanceStash.WriteTab(new(), tab).ToArray());
        Assert.Equal("0021" + "0001" + tabHex + "0000" + "0000" + "0000" + "000000" + "00" + "00",
            Convert.ToHexString(InstanceProtocol.EnterArea([tab])));
    }

    [Fact]
    public void Tab_loaded_and_tab_list_layouts()
    {
        Assert.Equal("00C8" + "0003" + "00000803" + "00", Convert.ToHexString(InstanceStash.TabLoaded(3, 0x803)));
        Assert.Equal("0136" + "00" + "00" + "0000", Convert.ToHexString(InstanceStash.TabList([])));
    }

    [Fact]
    public void Client_stash_messages_are_framed()
    {
        // 0xc7 tab 4 of the personal stash, then 0xcc with two (tab, position) pairs, then a tick.
        byte[] data = Convert.FromHexString("00C7000400" + "00CC0002" + "00000001" + "00010000" + "001B01020304" + "00");
        var messages = InstanceProtocol.SplitClientMessages(data, out var partial, out var unknown);

        Assert.Equal([InstanceStash.RequestTabId, InstanceStash.ReorderTabsId, InstanceProtocol.TickId], messages.Select(m => m.Id));
        Assert.Equal(((ushort)4, (byte)0), InstanceStash.ReadRequestTab(messages[0].Bytes));
        var (kind, order) = InstanceStash.ReadReorderTabs(messages[1].Bytes);
        Assert.Equal(0, kind);
        Assert.Equal([((ushort)0, (ushort)1), ((ushort)1, (ushort)0)], order);
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Stash_object_blob()
    {
        Assert.Equal("0000" + "3F010000" + "10010000" + "00000000" + "0000" + "0000" + "00" + "00" + "00" + "00",
            Convert.ToHexString(new StashObject(319, 272).Blob()));
    }
    [Fact]
    public void Ctrl_click_into_a_tab_reads_the_live_capture()
    {
        byte[] live = Convert.FromHexString("00E00000000000F80000694F0000000000000000" + "00E3000200000109" + "00000000");
        var messages = InstanceProtocol.SplitClientMessages(live, out var partial, out var unknown);

        Assert.Equal([InstanceStash.MoveToTabId, InstanceStash.TakeFromTabId], messages.Select(m => m.Id));
        Assert.Equal(new InstanceStash.MoveToTab(0, 0xf8, 0, 0, 0x4f69, 0), InstanceStash.ReadMoveToTab(messages[0].Bytes));
        Assert.Equal(((ushort)2, 0x109u, (byte)0, (byte)0, (byte)0, (byte)0), InstanceStash.ReadTakeFromTab(messages[1].Bytes));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData(0, 0)]
    public void Withdrawal_bulk_boolean_is_separate_from_stash_kind_and_token(byte kind, byte bulk)
    {
        // Layout from the native 0xe3 builder/writer, not a live right-click capture.
        byte[] bytes = [0, 0xe3, 0, 7, 0x40, 0, 0, 1, kind, 0xa7, 0xcd, bulk];
        var messages = InstanceProtocol.SplitClientMessages(bytes, out var partial, out var unknown);

        var move = InstanceStash.ReadTakeFromTab(Assert.Single(messages).Bytes);

        Assert.Equal(7, move.Tab);
        Assert.Equal(0x40000001u, move.ItemId);
        Assert.Equal(kind, move.Kind);
        Assert.Equal(0xa7, move.TokenLo);
        Assert.Equal(0xcd, move.TokenHi);
        Assert.Equal(bulk, move.Bulk);
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Affinity_move_result_and_personal_tab_id_layouts()
    {
        Assert.Equal("00D5" + "01" + "0005" + "0A" + "03" + "00",
            Convert.ToHexString(InstanceStash.EncodeAffinityMoveResult(true, 5, 10, 3, InstanceStash.Personal)));
        Assert.Equal("00D5" + "00" + "FFFF" + "00" + "16" + "00",
            Convert.ToHexString(InstanceStash.EncodeAffinityMoveResult(false, InstanceStash.LockerTabIndex, 0, 0x16)));
        Assert.Equal("00D6" + "0004" + "0008" + "3030303030303035",
            Convert.ToHexString(InstanceStash.EncodePersonalStashTabId(4, "00000005")));
    }

    [Fact]
    public void Specialty_stash_and_collection_quick_move_layouts()
    {
        // C→S 0x00e2 (9B): tab=5, itemId=0x12345678, kind=0 (personal)
        byte[] e2 = Convert.FromHexString("00E2" + "0005" + "12345678" + "00");
        // C→S 0x002b (21B): kind=0, tab=15, subTabKey=0x0102030405060708 LE, x=3, y=2
        byte[] s2b = Convert.FromHexString("002B" + "00" + "000F" + "0807060504030201" + "00000003" + "00000002");
        // C→S 0x0114 (16B): itemId=0x42, tab=2, subTabKey=0x1122334455667788 LE
        byte[] s114 = Convert.FromHexString("0114" + "00000042" + "0002" + "8877665544332211");
        var messages = InstanceProtocol.SplitClientMessages([.. e2, .. s2b, .. s114], out var partial, out var unknown);

        Assert.Equal([InstanceStash.QuickPlaceInTabId, InstanceStash.SubTabClickId, InstanceStash.CollectionToStashId],
            messages.Select(m => m.Id));
        Assert.Equal(new InstanceStash.QuickPlaceInTab(5, 0x12345678u, 0), InstanceStash.ReadQuickPlaceInTab(messages[0].Bytes));
        Assert.Equal(new InstanceStash.SubTabClick(0, 15, 0x0102030405060708ul, 3, 2), InstanceStash.ReadSubTabClick(messages[1].Bytes));
        Assert.Equal(new InstanceStash.CollectionToStash(0x42u, 2, 0x1122334455667788ul), InstanceStash.ReadCollectionToStash(messages[2].Bytes));
        Assert.Empty(partial);
        Assert.Empty(unknown);
    }
}
