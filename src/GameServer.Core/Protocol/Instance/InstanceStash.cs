using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The stash (Agents/docs/protocol/18-stash.md). The tab list travels in S→C 0x21 (personal and trade tabs) and S→C 0x136
/// (one list again); a tab's contents are an inventory of the player's inventory manager (manager 0, Inventories.dat row
/// 26 <c>StashInventoryId</c>), sent when the client asks for the tab (C→S 0xc7) and marked loaded with S→C 0xc8. The
/// Stash object's click opens the panel with S→C 0x7b kind 1.
/// </summary>
public static class InstanceStash
{
    public const ushort TabListId = 0x136, TabLoadedId = 0xc8, RequestTabId = 0xc7, ReorderTabsId = 0xcc, MoveTabId = 0xd1,
        RenameTabId = 0xcd, ColourTabId = 0xce;

    /// <summary>S→C 0x7b kind that opens the personal stash (0x142163420 case 1 → listeners at instance +0x1178 →
    /// 0x140dcd5e0): u32 a = the Stash object, u32 b = an affinity (StashTabAffinityId row): the panel selects the first tab
    /// with that affinity bit; <see cref="NoAffinity"/> selects nothing. Kind 2 is the guild stash.</summary>
    public const byte OpenStashUi = 1;

    public const uint NoAffinity = 0x16;

    /// <summary>The 0x136 / 0xc7 / 0xc8 stash kind: the personal stash (0), the guild stash (1), trade tabs (2); instance
    /// +0xc498 / +0xc4b0 / +0xc4c8 (0x14216cf60).</summary>
    public const byte Personal = 0;
    /// <summary>The guild stash: 0x136 / 0xc8 kind 1; S→C 0x7b kind 2 opens its panel (listener 0x140dcd780, tabs at instance +0xc4b0).</summary>
    public const byte Guild = 1, OpenGuildStashUi = 2;

    /// <summary>The guild stash tabs' inventory manager in S→C 0xc0 (instance +0xcb90); the personal stash uses 0.</summary>
    public const byte GuildManager = 2;

    /// <summary>Inventories.dat row 26 (StashInventoryId, kind 0x11): the row of every stash tab inventory. The register
    /// (0x141d625f0) files every row but this one under the row, so any number of tabs can share it.</summary>
    public const byte TabRow = 26, TabKind = 0x11;

    public static InstanceInventories.Slot TabSlot(byte width, byte height) => new(TabRow, TabKind, width, height);

    /// <summary>Tab record +0x3d flag bits (read by 0x141d4d1f0; meaning from the panel 0x140a88710 and the tab settings
    /// 0x14025b420).</summary>
    [Flags]
    public enum TabFlags : byte
    {
        None = 0,
        /// <summary>→ panel tab +0x38 bit 3.</summary>
        Bit0 = 0x01,
        /// <summary>A premium tab: the tab settings can rename and recolour it.</summary>
        Premium = 0x02,
        /// <summary>→ panel tab +0x38 bit 6 (remove-only in the real game, not checked).</summary>
        Bit3 = 0x08,
        /// <summary>A u8 (+0x3c) follows the record.</summary>
        HasByte = 0x10,
        /// <summary>A u64 (+0x00) follows; such a tab is left out of the panel.</summary>
        Hidden = 0x20,
        /// <summary>Four affinity bytes (+0x3f, a bitset of StashTabAffinityId rows) follow.</summary>
        Affinities = 0x40,
    }

    /// <summary>One stash tab (a 0x68-byte record, 0x141d4d1f0). Index: its place in the list, which C→S 0xc7 and S→C 0xc8
    /// name. Inventory: its inventory id in manager 0 (+0x28). Type: a StashType row (+0x34: 0 NormalStash, 1
    /// PremiumStash, 3 CurrencyStash, 7 QuadStash, 16 Folder …). Position: its place among the tab buttons (+0x38; the
    /// panel sends C→S 0xcc when it differs from the button order). Parent: the folder tab's index, 0xffff none (+0x3a).
    /// Loaded (flag bit 9, +0x3e bit 1): the contents are known; S→C 0xc8 sets it.</summary>
    public sealed record Tab(ushort Index, string Name, uint Inventory, byte Type, byte R, byte G, byte B,
        TabFlags Flags = TabFlags.None, ushort Position = 0, ushort Parent = 0xffff, uint Affinities = 0, bool Loaded = false,
        string Id = "", uint U2c = 0, ulong HiddenKey = 0);

    /// <summary>A tab record (0x141d4d1f0): u32 BE inventory id (+0x28), u32 BE (+0x2c, copied to the panel tab's +0x30),
    /// wstr name (+0x08), str id (+0x48), u16 BE position (+0x38), two flag bytes (+0x3d, +0x3e), u16 BE parent (+0x3a),
    /// u8 R, G, B (+0x30..+0x32), u8 type (+0x34); then by flags: u64 (0x20), u8 (0x10), 4 affinity bytes (0x40).</summary>
    public static LoginWriter WriteTab(LoginWriter w, Tab t)
    {
        var flags = t.Flags & ~(TabFlags.Hidden | TabFlags.HasByte | TabFlags.Affinities);
        if (t.Affinities != 0) flags |= TabFlags.Affinities;
        if (t.HiddenKey != 0) flags |= TabFlags.Hidden;
        w.U32(t.Inventory).U32(t.U2c).WStr(t.Name);
        StrNarrow(w, t.Id);
        w.U16(t.Position).U8((byte)flags).U8((byte)(t.Loaded ? 0x02 : 0)).U16(t.Parent).U8(t.R).U8(t.G).U8(t.B).U8(t.Type);
        if (t.HiddenKey != 0) w.Bytes(BitConverter.GetBytes(t.HiddenKey));
        // The affinity bitset is copied raw (memcpy to +0x3f), so it goes little-endian.
        if (t.Affinities != 0) w.U8((byte)t.Affinities).U8((byte)(t.Affinities >> 8)).U8((byte)(t.Affinities >> 16)).U8((byte)(t.Affinities >> 24));
        return w;
    }

    /// <summary>A tab list (0x1421ba150): u16 BE count, then the records.</summary>
    public static LoginWriter WriteTabs(LoginWriter w, IReadOnlyList<Tab> tabs)
    {
        w.U16((ushort)tabs.Count);
        foreach (var t in tabs) WriteTab(w, t);
        return w;
    }

    /// <summary>The narrow string of 0x1408402a0: u16 BE length, then the bytes.</summary>
    static void StrNarrow(LoginWriter w, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        w.U16((ushort)bytes.Length).Bytes(bytes);
    }

    /// <summary>
    /// S→C 0x136 (class 0x1435dc7a0, read 0x1421a3350; dispatcher case 0x57): one stash list again. u8 kind (+0x28: 0
    /// personal, 1 guild, 2 trade), u8 (+0x29: which listener is told, instance +0x8478 when set, else +0x83f8), the tab
    /// list. The panel rebuilds its tabs.
    /// </summary>
    public static byte[] TabList(IReadOnlyList<Tab> tabs, byte kind = Personal, byte u29 = 0) =>
        WriteTabs(new LoginWriter().U16(TabListId).U8(kind).U8(u29), tabs).ToArray();

    /// <summary>
    /// S→C 0xc8 (class 0x1435dc680, read 0x1421a45b0; dispatcher case 0x3b): a tab's contents arrived. u16 BE tab index,
    /// u32 BE its inventory id (→ tab +0x28), u8 kind. The handler sets the tab's loaded bit and tells the panel
    /// (instance +0x8378 → 0x140a87740), which builds the tab's page on the inventory.
    /// </summary>
    public static byte[] TabLoaded(ushort index, uint inventoryId, byte kind = Personal) =>
        new LoginWriter().U16(TabLoadedId).U16(index).U32(inventoryId).U8(kind).ToArray();

    /// <summary>
    /// C→S 0xc7 (5 bytes, vtable 0x1435bff20, write 0x141f95fa0): u16 BE tab index, u8 kind. Queued by 0x142167010 when a
    /// tab is selected whose page is not built, and when the panel opens (0x140a8d1b0), and sent one per 200 ms by the tick
    /// 0x142148880.
    /// </summary>
    public static (ushort Index, byte Kind) ReadRequestTab(ReadOnlySpan<byte> m) =>
        ((ushort)(m[2] << 8 | m[3]), m[4]);

    public const int RequestTabLength = 5;

    /// <summary>C→S 0xcc (vtable 0x1435c0438, write 0x141f96130; 0x1421670f0): the tab buttons' order. u8 kind, u8 n, n ×
    /// (u16 BE tab index, u16 BE position). Length 4 + 4n, or null while the count byte has not arrived.</summary>
    public static int? ReorderTabsLength(ReadOnlySpan<byte> m) => m.Length < 4 ? null : 4 + 4 * m[3];

    /// <summary>C→S 0xcd (vtable 0x1435c00d8, write 0x141f962a0; built by the tab settings 0x14025b420): a premium tab
    /// renamed. u8 kind, u16 BE tab, u64 (0), wstr name.</summary>
    public static int? RenameTabLength(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16(); r.U8(); r.U16(); r.U32(); r.U32();
        r.WStr();
        return r.Short ? null : r.Position;
    }

    public static (byte Kind, ushort Tab, string Name) ReadRenameTab(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m);
        r.U16();
        byte kind = r.U8();
        ushort tab = r.U16();
        r.U32(); r.U32();
        return (kind, tab, r.WStr());
    }

    /// <summary>C→S 0xce (17 bytes, vtable 0x1435bf9a8, write 0x141f96670; the tab settings 0x14025b420): a premium tab's
    /// colour. u8 kind, u16 BE tab, u64 (0), u32 BE colour, which the client keeps in the tab record's +0x2c (sent back
    /// as <see cref="Tab.U2c"/>).</summary>
    public const int ColourTabLength = 17;

    public static (byte Kind, ushort Tab, uint Colour) ReadColourTab(ReadOnlySpan<byte> m) =>
        (m[2], (ushort)(m[3] << 8 | m[4]), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[13..]));

    public static (byte Kind, List<(ushort Index, ushort Position)> Order) ReadReorderTabs(ReadOnlySpan<byte> m)
    {
        var order = new List<(ushort, ushort)>();
        for (int i = 0, at = 4; i < m[3]; i++, at += 4) order.Add(((ushort)(m[at] << 8 | m[at + 1]), (ushort)(m[at + 2] << 8 | m[at + 3])));
        return (m[2], order);
    }

    /// <summary>C→S 0xd1 (7 bytes, vtable 0x1435c0198, write 0x141f96c50; 0x142167370): a tab into a folder. u16 BE tab,
    /// u16 BE folder (0xffff: out of it), u8 kind.</summary>
    public const int MoveTabLength = 7;

    public const ushort MoveToTabId = 0xe0, TakeFromTabId = 0xe3;

    /// <summary>C→S 0xe0: Ctrl + click on an inventory item while a stash tab is shown. Tab: the shown tab's index (the
    /// stash page's +0x4a4); Kind: the page's stash kind (+0x491); Flags: bit 0, bit 1 (unknown, 0 live); Token: a u16
    /// that changes per request; U64: the page's optional u64 (0 live).</summary>
    public sealed record MoveToTab(ushort Tab, uint ItemId, byte Flags, byte Kind, ushort Token, ulong U64);

    /// <summary>
    /// C→S 0xe0 (20 bytes, vtable 0x1435c0878, write 0x141f97560; built by 0x142168ab0, called from the stash page
    /// 0x140b1e6d0 / 0x140b617e0 and the inventory grid's Ctrl + click 0x140313ba0): u16 BE tab, u32 BE item id, u8 flags,
    /// u8 kind, u16 token (two raw bytes), u64 raw. The item's inventory is not sent: the server knows where it is.
    /// Live 2026-10-19: <c>00E0 0000 000000F8 00 00 694F 0000000000000000</c>.
    /// </summary>
    public static MoveToTab ReadMoveToTab(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U16(), r.U32(), r.U8(), r.U8(), (ushort)(r.U8() | r.U8() << 8),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(m[12..]));
    }

    public const int MoveToTabLength = 20;

    /// <summary>
    /// C→S 0xe3 (12 bytes, vtable 0x1435c0500, write 0x141f97e40; built by 0x142168e20): an item out of a stash tab. u16
    /// BE tab, u32 BE item id, u8 kind, u16 raw token (low byte first), u8 bulk (0: one stack; 1: Ctrl + right click).
    /// The builder copies its 6th argument to the last byte; 0x140312810 forwards the click's bulk boolean there.
    /// The special tabs'
    /// pages (0x140b3c040, 0x140b57810) and 0x14031b950 send it; whether a normal tab's Ctrl + click does is not checked.
    /// </summary>
    public static (ushort Tab, uint ItemId, byte Kind, byte TokenLo, byte TokenHi, byte Bulk) ReadTakeFromTab(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return (r.U16(), r.U32(), r.U8(), r.U8(), r.U8(), r.U8());
    }

    public const int TakeFromTabLength = 12;

    public const ushort MoveToAffinityTabId = 0xe1, SetAffinitiesId = 0xd3;

    /// <summary>C→S 0xe1: Ctrl + click on an inventory item with an affinity tab while the stash is open. Tabs: the tabs
    /// the client picked for it, each with the affinity (StashTabAffinityId row) it matched.</summary>
    public sealed record MoveToAffinityTab(uint ItemId, byte Flags, byte U8, ushort Token, IReadOnlyList<(ushort Tab, byte Affinity)> Tabs);

    /// <summary>
    /// C→S 0xe1 (vtable 0x1435c00f8, write 0x141f97810): u32 BE item id, u8 flags, u8, u16 token (raw, changes per
    /// request), u8 n, n × (u16 BE tab, u8 affinity). The layout is read from live bytes (2026-10-20, Ctrl + click on a
    /// Scroll of Wisdom, an Armourer's Scrap, Orbs of Alteration and Transmutation with the Currency tab at index 5):
    /// <c>00E1 00000008 00 00 225E 01 0005 03</c>, always 14 bytes with one entry.
    /// </summary>
    public static MoveToAffinityTab ReadMoveToAffinityTab(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        uint item = r.U32();
        byte flags = r.U8(), u8 = r.U8();
        ushort token = (ushort)(r.U8() | r.U8() << 8);
        var tabs = new List<(ushort, byte)>();
        for (int i = 0, n = r.U8(); i < n; i++) tabs.Add((r.U16(), r.U8()));
        return new(item, flags, u8, token, tabs);
    }

    /// <summary>The length of a C→S 0xe1 at the start of <paramref name="m"/>, or null until its count arrived.</summary>
    public static int? MoveToAffinityTabLength(ReadOnlySpan<byte> m) => m.Length < 11 ? null : 11 + 3 * m[10];

    /// <summary>C→S 0xe1 flags bit: Ctrl + right click (Ctrl + left click sends 0). Live 2026-10-23, one Ctrl + right click on
    /// an orb: <c>00E1 000037D5 02 00 6A7D 01 0005 03</c>. The server moves every stack of that type, as the player asked.</summary>
    public const byte MoveAllOfType = 0x02;

    /// <summary>C→S 0xd3: the tab settings' affinities, confirmed (the window's check mark).</summary>
    public sealed record SetAffinities(ushort Tab, byte Kind, IReadOnlyList<(byte Affinity, bool On)> Changes);

    /// <summary>
    /// C→S 0xd3 (class 0x1435c0338, read 0x141f96f70, write 0x141f96da0; built by 0x1421674c0): u16 BE tab, u8 kind, u8 n,
    /// n × (u8 affinity, u8 on). Live 2026-10-20 on the Currency tab (index 5): <c>00D3 0005 00 01 03 01</c> (Currency on).
    /// </summary>
    public static SetAffinities ReadSetAffinities(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        ushort tab = r.U16();
        byte kind = r.U8();
        var changes = new List<(byte, bool)>();
        for (int i = 0, n = r.U8(); i < n; i++) changes.Add((r.U8(), r.U8() != 0));
        return new(tab, kind, changes);
    }

    /// <summary>The length of a C→S 0xd3 at the start of <paramref name="m"/>, or null until its count arrived.</summary>
    public static int? SetAffinitiesLength(ReadOnlySpan<byte> m) => m.Length < 6 ? null : 6 + 2 * m[5];
}

/// <summary>
/// The stash chest, <c>Metadata/MiscellaneousObjects/Stash</c>: Positioned (object_size 3, blocking), BaseEvents (server
/// only), Animated (chest_v01_01.ao from the type), MinimapIcon (StashPlayer), InteractionAction; client Render
/// ("ObjectDisplayTextStash"), Targetable, StateMachine ("open"). Not a tile object: the server places it.
/// </summary>
public sealed record StashObject(int GridX, int GridY, float Rotation = 0)
{
    public const string MetadataPath = "Metadata/MiscellaneousObjects/Stash";
    /// <summary>The guild stash object: the same components (Positioned, BaseEvents, Animated, MinimapIcon,
    /// InteractionAction), so the same blob.</summary>
    public const string GuildMetadataPath = "Metadata/MiscellaneousObjects/GuildStash";

    public byte[] Blob()
    {
        var w = new BlobWriter();
        w.U8(0).U8(0);                                                              // object flags, base data entry count
        // As an NPC's: x, y, rotation, u16 team 0, flags0, flags1 0x02 + two u8 (shown).
        w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8(0).U8(0x02).U8(0).U8(1);
        w.U8(0);                                                                    // Animated: the type's model
        w.U8(0);                                                                    // MinimapIcon flags
        w.U8(0);                                                                    // InteractionAction flags
        w.U8(0);                                                                    // tasks
        return w.ToArray();
    }
}
