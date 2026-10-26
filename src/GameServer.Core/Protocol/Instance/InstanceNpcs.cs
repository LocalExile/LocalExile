using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>NPC messages (Agents/docs/protocol/17-npcs.md).</summary>
public static class InstanceNpcs
{
    public const ushort TalkId = 0x84;
    // HideoutSelectionWindow in the native screen-name table at 0x142fc5920.
    public const byte HideoutSelectionUi = 0x22;

    /// <summary>C→S 0x85 (id only): the dialogue window was closed (Goodbye, Esc or walking away). Live 2026-10-19: sent
    /// after every 0x84, alone in its chunk.</summary>
    public const ushort DialogueClosedId = 0x85;

    /// <summary>C→S 0x82 (3 bytes, class 0x14345ef10 "u8 message", sent by NPCDialogueWindow 0x140722960): u8 = the chosen
    /// NPCTalk row's index among the NPC's rows (column 1). The client handles NPCTalkCategory 5 (Topic), 0x19 and 0x26
    /// itself and sends 0x82 for the others.</summary>
    public const ushort OptionId = 0x82;
    public const int OptionLength = 3;

    public static byte ReadOption(ReadOnlySpan<byte> m) => m[2];

    public const ushort TalkTextId = 0x87;

    /// <summary>S?C 0x87: u32 reward inventory id (manager 1), u16 QuestRewardOffers row, with bit 15 an optional flag.
    /// Send 0xc0 for that inventory before opening it. The first field is NOT the NPC object id: a missing inventory
    /// crashes the reward window at 0x140c2bc20. Live verified 2026-10-25 with offers 0 and 8, inventory 0x600.
    /// Selecting a reward uses C?S 0x88 (or Ctrl+click 0x8a), as with a shop.</summary>
    public static byte[] OpenQuestReward(uint inventoryId, ushort offer, bool flag = false) =>
        new LoginWriter().U16(TalkTextId).U32(inventoryId).U16((ushort)((offer & 0x7fff) | (flag ? 0x8000 : 0))).ToArray();

    // Retain the old public entry point for callers; its historical name did not describe the message's purpose.
    public static byte[] TalkText(uint inventoryId, ushort offer, bool flag = false) => OpenQuestReward(inventoryId, offer, flag);

    public const ushort BuyId = 0x88;

    /// <summary>C→S 0x88: buy an item from an open shop tab.</summary>
    public sealed record Buy(uint ItemId, uint InventoryId, byte X, byte Y, byte U8, byte Flags, uint Extra);

    /// <summary>
    /// C→S 0x88 (class 0x1435c0778, write 0x141f95380, sent by 0x140314320; live 2026-10-19 on a click in Nessa's shop):
    /// u32 BE shop item id (+0x10), u32 BE destination inventory (+0x14, the window's +0x5c0: 1 = main inventory; masked like
    /// 0x28's), u8 x, u8 y (the free cell the client chose), u8 (+0x1e), u8 flags (bit0 +0x1f, bit1 +0x20), compact uint
    /// (+0x18).
    /// </summary>
    public static Buy ReadBuy(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        uint item = r.U32(), inventory = r.U32() & 0xc01fffff;
        byte x = r.U8(), y = r.U8(), u8 = r.U8(), flags = r.U8();
        int pos = 2 + 12;
        return new Buy(item, inventory, x, y, u8, flags, CompactUInt(m[pos..]));
    }

    /// <summary>The length of a C→S 0x88 at the start of <paramref name="data"/>, or null until its compact int arrived.</summary>
    public static int? BuyLength(ReadOnlySpan<byte> data) =>
        data.Length < 15 ? null : 14 + CompactUIntLength(data[14]) is int n && data.Length >= n ? n : null;

    public const ushort CtrlBuyId = 0x8a;

    /// <summary>C→S 0x8a: Ctrl + click on an item in an open shop tab: buy it into the main inventory (the server picks the
    /// cell).</summary>
    public sealed record CtrlBuy(uint ItemId, byte U8, byte Flags, uint Extra, ushort Token);

    /// <summary>
    /// C→S 0x8a (dispatcher case 0x8a; 11 bytes in every live capture, 2026-10-20 in Nessa's shop): u32 BE shop item id,
    /// u8, u8 flags, compact uint (as 0x88's), u16 token (raw, changes per request). Read from live bytes:
    /// <c>008A 00000061 00 00 00 A777</c>.
    /// </summary>
    public static CtrlBuy ReadCtrlBuy(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        uint item = r.U32();
        byte u8 = r.U8(), flags = r.U8();
        int at = 8, n = CompactUIntLength(m[at]);
        return new(item, u8, flags, CompactUInt(m[at..]), (ushort)(m[at + n] | m[at + n + 1] << 8));
    }

    /// <summary>The length of a C→S 0x8a at the start of <paramref name="data"/>, or null until its compact int arrived.</summary>
    public static int? CtrlBuyLength(ReadOnlySpan<byte> data) => data.Length < 9 ? null : 8 + CompactUIntLength(data[8]) + 2;

    static int CompactUIntLength(byte first) => first < 0x80 ? 1 : first < 0xc0 ? 2 : first < 0xe0 ? 3 : first < 0xf0 ? 4 : 5;

    static uint CompactUInt(ReadOnlySpan<byte> b) => CompactUIntLength(b[0]) switch
    {
        1 => b[0],
        2 => (uint)((b[0] & 0x3f) << 8 | b[1]),
        3 => (uint)((b[0] & 0x1f) << 16 | b[1] << 8 | b[2]),
        4 => (uint)((b[0] & 0x0f) << 24 | b[1] << 16 | b[2] << 8 | b[3]),
        _ => (uint)(b[1] << 24 | b[2] << 16 | b[3] << 8 | b[4]),
    };

    /// <summary>S→C 0x7b kind 0x5f: the character's passive tree (UI +0x650; listener on instance +0x2078 = 0x140dc8750). A
    /// non-zero first u32 shows it in mode 2 (0x1409de680), the NPC script's SendOpenScreen( CharacterPassiveTreeScreen, 1 ).
    /// Not 0x11 (Atlas passive tree, UI +0x668, 0x140dcdf80) and not 0x45 (UI +0x678, 0x140dc7e90).</summary>
    public const byte PassiveTreeUi = 0x5f;

    /// <summary>S→C 0x7b kind 0x1c: the Delve chart (Niko's SendOpenScreen( DelveMapScreen, 1 )). The client's screen
    /// table at 0x142fc5920 (24-byte entries: UTF-16 name, its length, the 0x7b kind) lists it; the same table gives
    /// CharacterPassiveTreeScreen 0x5f and AtlasPassiveTreeScreen 0x11, as known.</summary>
    public const byte DelveMapUi = 0x1c;

    public const ushort TradeOpenId = 0xb8, TradeCloseId = 0xb9;

    /// <summary>
    /// S→C 0xb8 (instance group, class 0x1435dbf00, write 0x1421a2960 → 0x141d6acc0, read → 0x141d6aeb0; dispatcher case
    /// 0x2f stores the struct at instance +0xce90 and calls the listeners at +0x81f0, the in-game UI's 0x140dccdf0): open a
    /// trade. Wire: u32 BE <paramref name="other"/> (struct +0x08; 0 = an NPC sale: the UI closes the dialogue, plays
    /// "SellingItem" and shows VendorSell, UI +0x7c8), u32 BE <paramref name="mine"/> (+0x00), u32 BE <paramref name="theirs"/>
    /// (+0x04), u8 flags (+0x50), wstr name (+0x10), u32 BE NPCs row (+0x30, 0xffffffff none), u16 (+0x52), u32 BE
    /// TradeWindowVisuals row (+0x40, 0xffffffff none), u16 (+0x54), u16 (+0x56). Being tested: mine/theirs as the offer
    /// inventories (manager 1).
    /// </summary>
    /// <summary>S→C 0xb9 (class 0x1435bf6f0, read 0x141f905e0; dispatcher case 0x30 → listeners at instance +0x8270, the
    /// in-game UI's 0x140dcd370): the trade ended. u32 BE trade (0 = the NPC sale: VendorSell closes), u8 accepted (plays
    /// "AcceptItem").</summary>
    public static byte[] TradeClose(uint other, bool accepted) => new LoginWriter().U16(TradeCloseId).U32(other).U8((byte)(accepted ? 1 : 0)).ToArray();

    public const ushort TradeOfferId = 0xb2;
    public const int TradeOfferLength = 16;

    /// <summary>C→S 0xb2: an item put into the player's trade offer.</summary>
    public sealed record TradeOffer(uint InventoryId, uint ItemId, ushort U16, byte X, byte Y, ushort Token);

    /// <summary>
    /// C→S 0xb2 (16 bytes; class 0x1435bfc78, write 0x141f95b40, sent by 0x142166560; live 2026-10-19 on a drop into the
    /// sell window's "Your Offer"): u32 BE source inventory (13 = the cursor), u32 BE item id, u16 BE (0), u8 x, u8 y (the
    /// offer cell), u16 (the sender's last argument, also passed to the listeners at instance +0xa178; it changed between
    /// drops). The target is the open trade's own offer.
    /// </summary>
    public static TradeOffer ReadTradeOffer(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new(r.U32() & 0xc01fffff, r.U32(), r.U16(), r.U8(), r.U8(), r.U16());
    }

    public const ushort TradeTakeBackId = 0xb3, TradeMoveAllId = 0xb5;
    public const int TradeTakeBackLength = 12, TradeMoveAllLength = 12;

    /// <summary>
    /// C→S 0xb3 (12 bytes, sent by 0x142166750) and 0xb5 (12 bytes, class 0x1435bf828, write 0x141f95e50, sent by
    /// 0x142166830): Shift + click and Ctrl + Shift + click on an item in a grid while a trade is open (the grid click
    /// handler 0x1402ddbc0). 0xb3: u32 BE item id, u32 BE the grid's inventory id, u16. 0xb5: u32 BE item id, u32 BE the
    /// grid's inventory id, u8, u8. The item moves to the other side (inventory ↔ offer). Live 2026-10-19 a click on an
    /// item in "Your Offer" sent 0xb3 with inventory 0.
    /// </summary>
    public static (uint ItemId, uint InventoryId) ReadTradeMove(ReadOnlySpan<byte> m) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]),
         System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[6..]) & 0xc01fffff);

    public const ushort TradeAcceptId = 0xb6, TradeUnacceptId = 0xb7;
    public const int TradeAcceptLength = 6, TradeUnacceptLength = 4;

    /// <summary>C→S 0xb6 (6 bytes, class 0x14316c420 "u32 message", sent by the Accept button 0x140c378d0 when 0xb8's flags
    /// have 0x10 and not 0x04): u32 BE the revision of the other side's offer inventory (its +0x1e8, the last 0xbf
    /// revision), so the server can tell the player accepted what it showed. C→S 0xb7 (4 bytes, class 0x1435bf7e8):
    /// Accept pressed again after accepting (un-accept), u16.</summary>
    public static uint ReadTradeAccept(ReadOnlySpan<byte> m) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]);

    /// <summary>0xb8 flags (struct +0x50) bit 4: the trade window's status (0x140c37140) does not ask to mouse over each
    /// of the other side's items before Accept; without it Accept stays disabled ("Mouse over each item to enable
    /// accept").</summary>
    public const byte TradeNoMouseOver = 0x10;

    /// <summary>0xb8 flags bit 0: the trade is the active one. The UI state's listener (0x140d62ec0) keeps the first u32
    /// (struct +0x08, the other party; 0 for an NPC sale) at +0x984 with +0x988 = 1, which Ctrl + click on an inventory
    /// item (0x14030e3e0) needs to send 0xb2 with x = y = 0xff ("into the offer"); without it the click goes through the
    /// other panels' checks and sends nothing (live 2026-10-19). S→C 0xb9 with the same id clears it (0x140d62e90).</summary>
    public const byte TradeActive = 0x01;

    public const ushort TradeAnswerId = 0xb1;
    public const int TradeAnswerLength = 7;

    /// <summary>C→S 0xb1 (7 bytes): u32 BE trade (0 for the NPC sale), u8. Live 2026-10-19 the sell window's Cancel sent
    /// it with 0; Accept is taken to be the same message with 1.</summary>
    public static (uint Other, byte Answer) ReadTradeAnswer(ReadOnlySpan<byte> m) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6]);

    public static byte[] TradeOpen(uint other, uint mine, uint theirs, byte flags, string name, int npcRow, int visualsRow = -1) =>
        new LoginWriter().U16(TradeOpenId).U32(other).U32(mine).U32(theirs).U8(flags).WStr(name).U32((uint)npcRow).U16(0)
            .U32((uint)visualsRow).U16(0).U16(0).ToArray();

    public const ushort DialogueId = 0x8c;

    /// <summary>A choice in an S→C 0x8c dialogue: compact int id (+0x24), wstr text, u8 enabled (+0x20).</summary>
    public sealed record Choice(int Id, string Text, bool Enabled = true);

    /// <summary>
    /// S→C 0x8c (instance group, class 0x1435dc720, read 0x1421a1f60, dispatcher case 0x1f): u32 BE NPC object id (+0x28),
    /// u16 BE (+0x2c, passed to the listeners at instance +0x1570), u8 flag (+0x2e), u16 BE n + n × u32 BE (+0x10, being tested
    /// as NPCTextAudio rows), u16 BE m + m × (compact int, wstr, u8) choices (+0x30). The dispatcher sets the dialogue state
    /// (+0xd7b8 = 2) and calls the listeners at +0x15f0 with both lists and the NPC (0x142171be0); the in-game UI's
    /// (0x140dcccf0) closes the dialogue windows, opens the inventory and a dialogue panel (UI +0x7b0, or +0x7b8 for the new
    /// style) with them. Live 2026-10-19 (lines [9], no choices) the client stopped. Not used.
    /// </summary>
    public static byte[] Dialogue(uint npcObjectId, ushort value, bool flag, IReadOnlyList<uint> lines, IReadOnlyList<Choice> choices)
    {
        var w = new LoginWriter().U16(DialogueId).U32(npcObjectId).U16(value).U8((byte)(flag ? 1 : 0));
        w.U16((ushort)lines.Count);
        foreach (uint l in lines) w.U32(l);
        w.U16((ushort)choices.Count);
        foreach (var c in choices)
        {
            InstanceProtocol.CompactUInt(w, (uint)c.Id);
            w.WStr(c.Text).U8((byte)(c.Enabled ? 1 : 0));
        }
        return w.ToArray();
    }

    /// <summary>S→C 0x84 "NPC dialogue" byte that opens the NPC's dialogue window (0x142149db0: the +0x13f8 listeners →
    /// 0x140dcc720 → NPCDialogueWindow).</summary>
    public const byte OpenDialogue = 0xff;

    /// <summary>
    /// S→C 0x84 (world group, class 0x1435dd7d8, read 0x141fddcb0; world handler 0x142187480 → 0x142149db0): object header
    /// (u16 id, u32 BE object id → +0x10, u32, u16), u8 → +0x20. Object id 0 closes the dialogue. With 0xff the client opens
    /// the NPC's dialogue window on its option list; with an NPCTalk index (column 1) it looks the row up for the NPC
    /// (0x14225e4b0) and opens the window on that row's talk (dialogue state 2); an unknown row makes it answer 0x85.
    /// </summary>
    public static byte[] Talk(uint npcObjectId, byte mode = OpenDialogue) =>
        new LoginWriter().U16(TalkId).U32(npcObjectId).U32(0).U16(0).U8(mode).ToArray();
}

/// <summary>
/// An NPC (Agents/docs/protocol/17-npcs.md): a 0x2b9 object of an NPCs.dat type such as <c>Metadata/NPC/Act1/Tarkleigh</c>.
/// <c>Metadata/NPC/NPC</c> deserializes Positioned, Animated, BaseEvents, InteractionAction, NPC and MinimapIcon (client
/// Render + Targetable); 190 NPC types have exactly that list, others add Transitionable and StateMachine.
/// <para>NPC (slot 17 0x141e75050) reads two u8 (→ +0x20 has dialogue, +0x21 a component index or 0xff). BaseEvents reads nothing.</para>
/// <para><paramref name="Model"/>: an .ao for Animated when the type's own is missing or not the one the town room places
/// (Irasha's type is <c>Metadata/NPC/NPC</c>); <paramref name="Height"/>: a fixed world z (Positioned flags0 bit2), as the
/// room's record gives it.</para>
/// </summary>
public sealed record NpcObject(int GridX, int GridY, IReadOnlyList<string> Components, float Rotation = 0,
    StateMachineData? StateMachine = null, byte TransitionState = 1, string? Model = null, float? Height = null)
{
    /// <summary>Deserialized components of <c>Metadata/NPC/NPC</c> in read order (scripts/re/ot_components.py).</summary>
    public static readonly string[] BaseComponents = ["Positioned", "Animated", "BaseEvents", "InteractionAction", "NPC", "MinimapIcon"];

    static readonly HashSet<string> Supported = [.. BaseComponents, "Transitionable", "StateMachine"];

    /// <summary>Whether <see cref="Blob"/> can serialize a type with these deserialized components.</summary>
    public static bool CanSerialize(IReadOnlyList<string> components, bool haveStateMachine = false) =>
        components.Contains("NPC") && components.All(c => Supported.Contains(c) && (c != "StateMachine" || haveStateMachine));

    public byte[] Blob()
    {
        if (!CanSerialize(Components, StateMachine is not null)) throw new ArgumentException($"unsupported components: {string.Join(", ", Components)}");
        var w = new BlobWriter();
        w.U8(0);            // object flags
        w.U8(0);            // base data entry count
        foreach (string c in Components)
        {
            switch (c)
            {
                // As a monster's: x, y, rotation (+0x29c), u16 team (0: friendly), flags0, flags1 0x02 + shown.
                // flags0 bit2: a fixed height before flags1 0x02's two bytes: i32 −z / 7.8125 and f32 z (world z points
                // down), the live server's convention (ProjectileObject; 11-skills.md "Sign").
                case "Positioned":
                    w.I32(GridX).I32(GridY).F32(Rotation).U16(0).U8((byte)(Height is null ? 0 : 0x04)).U8(0x02);
                    if (Height is float z) w.I32((int)MathF.Round(-z / 7.8125f)).F32(z);
                    w.U8(0).U8(1);
                    break;
                // 0: the type supplies the model; bit0: a u32-counted UTF-16 .ao path.
                case "Animated": if (Model is null) w.U8(0); else w.U8(1).WStr32(Model); break;
                case "InteractionAction": w.U8(0); break;
                // +0x20: has dialogue (0x141e76d40 offers NPCTalk rows only when set); +0x21: index of a component that
                // must be alive (its +0x1a8 > 0) for the NPC to talk, 0xff = none.
                case "NPC": w.U8(1).U8(0xff); break;
                case "MinimapIcon": w.U8(0); break;
                case "Transitionable": w.U8((byte)(TransitionState & 0x7f)); break;
                case "StateMachine":
                    var sm = StateMachine!;
                    w.U8((byte)sm.SharedStates.Count).U8((byte)sm.SharedTimers);
                    foreach (int v in sm.SharedStates)
                    {
                        var lw = new LoginWriter();
                        InstanceProtocol.CompactInt(lw, v);
                        foreach (byte b in lw.ToArray()) w.U8(b);
                        w.U8(0);
                    }
                    for (int i = 0; i < sm.SharedTimers; i++) w.U32(0).U8(0);
                    break;
                // BaseEvents: no data.
            }
        }
        w.U8(0);            // tasks
        return w.ToArray();
    }
}
