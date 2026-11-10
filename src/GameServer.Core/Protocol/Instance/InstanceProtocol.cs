using System.Buffers.Binary;
using System.Security.Cryptography;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The instance (game server) connection, Agents/docs/re/010. Same framing as login (u16 BE id, fields, no length prefix) and
/// the same field encodings (<see cref="LoginWriter"/> / <see cref="LoginReader"/>). The client opens it after login
/// 0x14 and sends two plaintext messages (0x14214ee50): 0x0F ticket and 0x0E hello. The server answers 0x11 in
/// plaintext. From the byte after it, both directions are Salsa20 keyed by the 64 bytes from 0x14 (client case 1 of
/// dispatcher 0x142150ae0 → 0x140e022d0(key, isClient=1), the same construction as login).
/// </summary>
public static class InstanceProtocol
{
    public const ushort TicketId = 0x0f, HelloId = 0x0e, StartEncryptionId = 0x11, AreaInstanceId = 0x1d, EnterAreaId = 0x21;

    /// <summary>Length of the client's plaintext opening: 0x0F (2 + 4) followed by 0x0E (2 + 12 + 4 + 4 + 1 + 1).</summary>
    public const int OpeningLength = 30;

    /// <summary>S→C 0x11 (no fields, class 0x1431768d8): switches both directions to Salsa20.</summary>
    public static byte[] StartEncryption() => new LoginWriter().U16(StartEncryptionId).ToArray();

    /// <summary>
    /// S→C 0x21 (class 0x1435e65c8, write 0x1421a9710; dispatcher case 10): enter the area. Two stash tab lists
    /// (<see cref="InstanceStash.WriteTabs"/>: the personal stash → instance +0xc498, the trade tabs → +0xc4c8), vec16 of
    /// (u32, u32), wstr, 24 flag bits, u8 +0x7e, u8 bits (+0x7b/+0x7c/+0x7d), the rest empty or zero. Its handler
    /// (0x14215d6d0) needs the local player entity (0x22 + 0x2bb) to exist already.
    /// </summary>
    /// <remarks>Flag bit 0 (+0x78) is the active weapon set: set = the second (Weapon2 / Offhand2). The handler copies it
    /// to the session's +0xcd31 (the dispatcher's r13 is the session + 8, so its "[r13+0xcd29]"), as S→C 0xde does.</remarks>
    public static byte[] EnterArea(IReadOnlyList<InstanceStash.Tab>? stash = null, bool secondWeaponSet = false)
    {
        var w = new LoginWriter().U16(EnterAreaId);
        InstanceStash.WriteTabs(w, stash ?? []);
        return w.U16(0).U16(0).WStr("").Bytes([(byte)(secondWeaponSet ? 0x01 : 0), 0, 0]).U8(0).U8(0).ToArray();
    }

    /// <summary>C→S 0xdd (3 bytes, built by 0x14216faa0): swap the weapon set. u8: the set wanted (1 the second, 0 the
    /// first; the client sends "current set is the first"). It then waits (session +0xcfa4 = 1) for S→C 0xde.</summary>
    public const ushort SwapWeaponsId = 0xdd;

    /// <summary>C→S 0x197 (10 bytes, live 2026-10-23 while Cyclone was held): a channelled skill goes on, u32 BE x, u32 BE
    /// y (grid cells) of the point it is aimed at now (the cursor, not the player). C→S 0x199 (2 bytes): the channel ends
    /// (the button let go). Read from captures.</summary>
    public const ushort ChannelUpdateId = 0x197, ChannelEndId = 0x199;

    public static (int X, int Y) ReadChannelUpdate(ReadOnlySpan<byte> m) =>
        ((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]), (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[6..]));

    /// <summary>S→C 0xde (3 bytes, class 0x14346cd80, dispatcher case 0x42 at 0x1421554b5): the weapon set. u8 flags: bit 0
    /// the active set (→ session +0xcd31; a change tells the listeners at +0x72f8), bit 1 keeps the client waiting
    /// (session +0xcfa4 = 2), else the wait ends (0). Verified with a live trace, 2026-10-22.</summary>
    public const ushort WeaponSetId = 0xde;

    public static byte[] WeaponSet(bool second) => new LoginWriter().U16(WeaponSetId).U8((byte)(second ? 1 : 0)).ToArray();

    public const ushort TickId = 0x1b, PongId = 0x1c, AreaGeneratedId = 0x7d;

    /// <summary>
    /// S→C 0x1c (class 0x1435e77a0, read 0x141fa7150; dispatcher case 5 → 0x14215c280): acknowledges the client's 0x1b
    /// ticks. Fields: u16 server delay (ms, subtracted from the measured round trip), u16 index into the client's queue
    /// of send times (+0xc458; entries up to and including it are dropped), u32 (fed bit by bit into the stream's side
    /// channel at +0xd0; 0 for us). The client's "network: N ms" is now − oldest unacknowledged send, so without 0x1c it
    /// grows forever. One 0x1c with index 0 per received 0x1b keeps the queue empty.
    /// </summary>
    public static byte[] Pong(ushort serverDelayMs = 0, ushort ackIndex = 0, uint sideChannel = 0) =>
        new LoginWriter().U16(PongId).U16(serverDelayMs).U16(ackIndex).U32(sideChannel).ToArray();

    /// <summary>Encrypted C→S messages whose length is known: 0x1b tick (u8 counter, 4 bytes; 7 in all, sent every
    /// 100–500 ms in state 3 by 0x14214f1d0) and 0x7d area generated (two u32 hashes; 10 in all).</summary>
    public const ushort UseSkillAtLocationId = 0x25, ActionStartedId = 0x1ab;

    /// <summary>Encrypted C→S messages whose length is known (live captures 2026-10-16): 0x1b tick, 0x7d area generated,
    /// 0x25 use skill at location (16), 0x015a and 0x01ef (id only). 0x0099 is 4 bytes (class 0x1435cac90, write
    /// 0x141f9bc30: two u8, meaning unknown), sent after entering an area together with the chat channel joins
    /// (<see cref="InstanceChat.JoinChannelId"/>) and 0x0129 (id only); the 20 bytes once taken for 0x0099 were all four.</summary>
    public static int? KnownClientLength(ushort id) => id switch
    {
        InstanceDelve.UpgradesRequestId => 2, InstanceDelve.UpgradeId => 4, InstanceDelve.PurchaseId => 4,
        TickId => 7, AreaGeneratedId => 10, UseSkillAtLocationId => 16, 0x015a => 2, 0x01ef => 2, 0x0099 => 4, 0x0129 => 2,
        InstanceChat.LinkItemId => InstanceChat.LinkItemLength, InstanceChat.LinkOtherId => InstanceChat.LinkOtherLength,
        InstanceChat.LinkAnotherId => InstanceChat.LinkAnotherLength,
        // Primary 3.29.3c writers: 0x140e27920 (u16 + u8), 0x141f99e90 (u16 + 4/2/2/4/1/1 bytes).
        0x020a => 3, 0x0059 => 16, InstanceSocial.TradeRequestId => InstanceSocial.TradeRequestLength,
        InstanceSocial.TradeRequestAcceptId => InstanceSocial.TradeRequestAcceptLength,
        InstanceSocial.PartyAcceptId => InstanceSocial.PartyAcceptLength,
        InstanceSocial.PartyLeaveId => InstanceSocial.PartyLeaveLength, InstanceSocial.MapPortalAccessId => InstanceSocial.MapPortalAccessLength,
        InstanceChat.JoinChannelId => InstanceChat.JoinChannelLength, InstancePantheon.ChooseGodId => InstancePantheon.ChooseGodLength,
        InstanceWaypoints.VisitDelveHubId => InstanceWaypoints.VisitDelveHubLength, InstanceWaypoints.VisitSanctumId => InstanceWaypoints.VisitSanctumLength,
        InstanceWaypoints.VisitKingsmarchId => InstanceWaypoints.VisitKingsmarchLength, InstanceWaypoints.VisitHeistHubId => InstanceWaypoints.VisitHeistHubLength,
        InstanceWaypoints.VisitChayulaHubId => InstanceWaypoints.VisitChayulaHubLength,
        // The World panel's other hub icons (0x140aa3880): {u32 object, u8 flag} for 0x17c SynthesisHub, 0x17f HallsOfTheDead,
        // 0x180 Necropolis, 0x183 DeepwaterHub, 0x185 GuildHideout; read so the stream goes on (no travel yet). 0x19b: the
        // World panel's tab-change UI event (writer 0x140911160: the id alone).
        0x017c => 7, 0x017f => 7, 0x0180 => 7, 0x0183 => 7, 0x0185 => 7, 0x019b => 2,
        InstanceTransfer.UseSkillOnObjectId => 12, InstanceTransfer.ChooseInstanceId => 3, ResurrectId => 3, InstanceItems.DropItemId => 2,
        InstanceItems.PickUpItemId => 13, InstanceItems.PlaceItemId => 18,
        InstanceItems.InventoryRequestId => InstanceItems.InventoryRequestLength, InstanceItems.UseItemId => InstanceItems.UseItemLength, InstanceItems.UseItemOnId => InstanceItems.UseItemOnLength, InstanceItems.UseItemOnPassiveId => InstanceItems.UseItemOnPassiveLength, InstanceItems.UseFlaskId => InstanceItems.UseFlaskLength, InstanceItems.UseItemTypeOnId => InstanceItems.UseItemTypeOnLength,
        InstanceItems.ItemActionId => InstanceItems.ItemActionLength, InstanceItems.SplitStackId => InstanceItems.SplitStackLength, InstanceItems.SocketGemId => 16,
        InstanceItems.UnsocketGemId => 17, BindSkillId => 5, AttackInPlaceId => 4, SlotMask52Id => 4, ExitToCharacterSelectId => 2,
        InstanceItems.LevelUpGemId => 14, InstanceItems.LockGemId => InstanceItems.LockGemLength,
        InstanceEntities.AllocatePassiveId => 7, InstanceEntities.MasteryEffectId => InstanceEntities.MasteryEffectLength, InstanceEntities.RefundPassiveId => InstanceEntities.RefundPassiveLength,
        InstanceEntities.RefundAllPassivesId => 2, InstanceEntities.PanelToggleId => 3, InstanceWaypoints.TravelId => InstanceWaypoints.TravelLength, InstanceNpcs.DialogueClosedId => 2, InstanceNpcs.OptionId => InstanceNpcs.OptionLength, InstanceNpcs.TradeAnswerId => InstanceNpcs.TradeAnswerLength, InstanceNpcs.TradeOfferId => InstanceNpcs.TradeOfferLength, InstanceNpcs.TradeTakeBackId => InstanceNpcs.TradeTakeBackLength, InstanceNpcs.TradeMoveAllId => InstanceNpcs.TradeMoveAllLength, InstanceNpcs.TradeAcceptId => InstanceNpcs.TradeAcceptLength, InstanceNpcs.TradeUnacceptId => InstanceNpcs.TradeUnacceptLength, InstanceStash.RequestTabId => InstanceStash.RequestTabLength, InstanceStash.MoveTabId => InstanceStash.MoveTabLength, InstanceStash.MoveToTabId => InstanceStash.MoveToTabLength, InstanceStash.TakeFromTabId => InstanceStash.TakeFromTabLength, InstanceMapDevice.ActivateId => InstanceMapDevice.ActivateLength, InstanceMapDevice.SelectMapId => InstanceMapDevice.SelectMapLength, InstanceMapDevice.MoveId => InstanceMapDevice.MoveLength, InstanceMapDevice.SocketId => InstanceMapDevice.SocketLength, InstanceEntities.AtlasAllocateId => InstanceEntities.AtlasAllocateLength, InstanceEntities.AtlasRefundId => InstanceEntities.AtlasRefundLength,
        SwapWeaponsId => 3, ChannelUpdateId => 10, ChannelEndId => 2, LockstepMoveStartId => 15, LockstepMoveUpdateId => 10, LockstepMoveStopId => 2, LockstepUseOnObjectId => 11, LockstepSkillStopId => 4,
        UseInstantSkillAtLocationId => 12, UseInstantSkillId => 8,
        InstancePause.RequestId => InstancePause.RequestLength, InstanceMicrotransactions.SlotId => InstanceMicrotransactions.SlotLength,
        InstanceWaypoints.LabyrinthChosenId => InstanceWaypoints.LabyrinthChosenLength,
        InstanceWaypoints.AscendancyChosenId => InstanceWaypoints.AscendancyChosenLength,
        InstanceMicrotransactions.SkillPickId => InstanceMicrotransactions.SkillPickLength,
        InstanceMicrotransactions.RequestId => InstanceMicrotransactions.RequestLength,
        InstanceHideouts.SelectId => InstanceHideouts.SelectLength,
        InstanceHideoutEditing.MoveId => InstanceHideoutEditing.MoveLength,
        InstanceBench.CraftId => InstanceBench.CraftLength, InstanceBench.MoveId => InstanceBench.MoveLength, InstanceHarvest.CraftId => InstanceHarvest.CraftLength,
        InstanceCurrencyExchange.RequestOrdersId => InstanceCurrencyExchange.RequestOrdersLength, InstanceCurrencyExchange.PlaceOrderId => InstanceCurrencyExchange.PlaceOrderLength,
        InstanceCurrencyExchange.CloseOrderId => InstanceCurrencyExchange.CloseOrderLength, InstanceCurrencyExchange.ClaimId => InstanceCurrencyExchange.ClaimLength,
        InstanceCurrencyExchange.RequestMarketId => InstanceCurrencyExchange.RequestMarketLength, InstanceCurrencyExchange.RequestPartnersId => InstanceCurrencyExchange.RequestPartnersLength,
        InstanceCurrencyExchange.RequestFeeId => InstanceCurrencyExchange.RequestFeeLength, _ => null,
    };

    /// <summary>
    /// C→S 0x190 (12 bytes): use an instant skill at a ground point, without an action sequence.
    /// 0x140123640 selects target mode 4; vtable 0x1435cb150's writer 0x141f98850 writes id, x, y and skill
    /// in network byte order. Frostblink Q captured 0190 0000011A 000000DC 0103.
    /// </summary>
    public const ushort UseInstantSkillAtLocationId = 0x190;

    public static (uint X, uint Y, ushort SkillId) ReadUseInstantSkillAtLocation(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return (r.U32(), r.U32(), r.U16());
    }

    /// <summary>
    /// C→S 0x191 (8 bytes, live 2026-10-21): u32 target object id (the player's own for a self-cast), u16 skill id.
    /// An instant skill (Haste, Purity of Elements, Righteous Fire) has no action sequence.
    /// Sender 0x140123640 uses target modes 5/6; captured Haste: 0191 00001000 0105.
    /// </summary>
    public const ushort UseInstantSkillId = 0x191;

    public static (uint TargetId, ushort SkillId) ReadUseInstantSkill(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return (r.U32(), r.U16());
    }

    public const ushort LockstepMoveStartId = 0x018e, LockstepMoveUpdateId = 0x0192, LockstepMoveStopId = 0x0195;

    /// <summary>C→S 0x018f (11 bytes, writer 0x141f98da0: u32 object, u16 skill, u16 flags, u8): lockstep's use of a skill on
    /// an object, instead of 0x24. C→S 0x0196 (u16 skill): release that skill.</summary>
    public const ushort LockstepUseOnObjectId = 0x018f, LockstepSkillStopId = 0x0196;

    /// <summary>C→S 0x018f. Flags 0x0401 and u8 0 on a free player; flags 0x0001 and u8 3 for a request made during a swing,
    /// which GGG serves when the swing ends.</summary>
    public static (uint ObjectId, ushort SkillId, ushort Flags, byte Extra) ReadLockstepUseOnObject(byte[] b) =>
        (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(6)),
         BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(8)), b[10]);

    /// <summary>S→C 0x194 (id only, instance group, handled on arrival): tells the client to go AFK. It answers at once with
    /// C→S 0x16 <see cref="InstanceItems.AfkCommand"/> and the text "This player is AFK." and changes nothing else until a
    /// server replies to that, so it is a round trip the server can start.</summary>
    public static byte[] AfkRequest() => [0x01, 0x94];

    /// <summary>C→S 0x0196: the skill released.</summary>
    public static ushort ReadLockstepSkillStop(byte[] b) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(2));

    /// <summary>S→C 0x1ae: the channelled action of this sequence is over (object header, u16 sequence, u8 1).</summary>
    public static byte[] ChannelFinished(uint objectId, ushort sequence) =>
        new LoginWriter().U16(0x1ae).U32(objectId).U32(0).U16(0).U16(sequence).U8(1).ToArray();

    /// <summary>
    /// What the client sends to move instead of 0x25 when it runs in lockstep (Options → Network Mode; "auto" chose it
    /// on localhost, live 2026-10-18): 0x018e (15 bytes: i32, i32 offsets from the player, u16 skill id 0x2909 Move, u16
    /// flags 0x0400 / 0x0402, u8), then 0x0192 (10 bytes: two i32) and 0x0195 (id only). Lengths from 26 captures of each,
    /// every one alone in its chunk; the layout is inferred, not read from the client's serializers. The Gateway serves
    /// them whenever the client sends them: the client alone picks its Network Mode.
    /// </summary>
    public static bool IsLockstepMovement(ushort id) => id is LockstepMoveStartId or LockstepMoveUpdateId or LockstepMoveStopId;

    /// <summary>Anything only a client in lockstep sends for its actions.</summary>
    public static bool IsLockstepRequest(ushort id) => IsLockstepMovement(id) || id is LockstepUseOnObjectId or LockstepSkillStopId;

    /// <summary>
    /// C→S 0x018e (lockstep; sender 0x1421660b0 when the network mode +0x984 is 2): use a skill at a location, the target
    /// given relative to the player's cell (i32 dx, i32 dy), then u16 skill id, u16 action flags, u8. 0x0192 (sender
    /// 0x1421664c0) carries a new (dx, dy) while the button is held; 0x0195 (0x142166660) is the release.
    /// </summary>
    public static (int Dx, int Dy, ushort SkillId, ushort Flags, byte Extra) ReadLockstepMoveStart(byte[] b) =>
        (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(2)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(6)),
         BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(10)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(12)), b[14]);

    public const ushort FrameEndId = 0x02b7, FrameLengthId = 0x02b8;

    /// <summary>S→C 0x02b7 (world group, id only): the end of a server frame. In lockstep (network mode 2) the world's
    /// message intake 0x14218dc20 then queues one frame of the current length (world +0x11c) behind the messages received
    /// so far; the world runs its messages and steps its objects one queued frame at a time (0x14218a2a0 → 0x14218a650).
    /// Ignored in predictive mode.</summary>
    public static byte[] FrameEnd() => [FrameEndId >> 8, FrameEndId & 0xff];

    /// <summary>S→C 0x02b8 (world group): u8, the length in ms of the server frames that follow (0x141648b50 stores it
    /// at world +0x11c).</summary>
    public static byte[] FrameLength(byte ms) => [FrameLengthId >> 8, FrameLengthId & 0xff, ms];

    /// <summary>C→S 0x0192 (lockstep): the held skill's new target relative to the player's cell (i32 dx, i32 dy).</summary>
    public static (int Dx, int Dy) ReadLockstepMoveUpdate(byte[] b) =>
        (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(2)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(6)));

    public const ushort SkillBarId = 0x53, SkillBarSlots = 13;

    /// <summary>
    /// S→C 0x53 (instance group, class 0x1435e73a0, read 0x1421a5f70, dispatcher case 0x13): the skill bar, being tested.
    /// 26 raw bytes = 13 × u16 skill ids by slot (+0x10, copied with memcpy, so little-endian like the blobs), then two
    /// raw (little-endian) u16 slot bitmasks: +0x2a, "Always Attack Without Moving" (bit = slot, C→S 0x4f), and +0x2c
    /// (C→S 0x52). Case 0x13 copies them to instance +0xcfd0 / +0xcfd2 after the rebuild, so a bar sent with 0 clears
    /// both options. The skill bar's rebuild 0x140ae1d00 (bar vtable +0x390, subscribed to instance
    /// +0xaf78) takes exactly such an array: each id is looked up in the ActiveSkillManager (+0x158, by +0x40) and put in
    /// its slot (vtable +0x398), an unknown id clears the slot (+0x3a0). Slot 2 is the right mouse button (C→S 0x4e).
    /// </summary>
    public static byte[] SkillBar(IReadOnlyList<ushort> skillIds, ushort attackInPlaceSlots = 0, ushort slotMask52 = 0)
    {
        var w = new LoginWriter().U16(SkillBarId);
        for (int i = 0; i < SkillBarSlots; i++)
        {
            ushort id = i < skillIds.Count ? skillIds[i] : (ushort)0;
            w.U8((byte)id).U8((byte)(id >> 8));
        }
        return w.U8((byte)attackInPlaceSlots).U8((byte)(attackInPlaceSlots >> 8)).U8((byte)slotMask52).U8((byte)(slotMask52 >> 8)).ToArray();
    }

    public const ushort AttackInPlaceId = 0x4f, SlotMask52Id = 0x52;

    /// <summary>
    /// C→S 0x4f and 0x52 (4 bytes, class 0x1435ca380, writer 0x141f99a50): u8 slot, u8 on. 0x4f is a slot's "Always
    /// Attack Without Moving" (sender 0x140aecf00; the client sets bit slot of instance +0xcfd0 itself, live 2026-11-02
    /// 004F0301). 0x52 sets bit slot of +0xcfd2 (0x14216c100; binding a skill, 0x140adf180, clears it), meaning unknown.
    /// </summary>
    public static (byte Slot, bool On) ReadSlotOption(ReadOnlySpan<byte> m) => (m[2], m[3] != 0);

    public const ushort BindSkillId = 0x4e;

    /// <summary>
    /// C→S 0x4e (5 bytes): the player put a skill on the skill bar. u8 slot (2 = right mouse button, live 2026-10-18),
    /// u16 skill id (the granted skill's +0x40; 0x0103 = Fireball). How the server gives the bar back on login is not known yet.
    /// </summary>
    public static (byte Slot, ushort SkillId) ReadBindSkill(ReadOnlySpan<byte> m) =>
        (m[2], System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(m[3..]));

    /// <summary>
    /// C→S 0x25 (class 0x1435ca5f0, sender 0x14216ccb0): use a skill at a ground location, e.g. Move on a left click.
    /// u32 x, u32 y (target grid cell, action +0x130/+0x134), u16 skill id (the granted skill's +0x40; Move = 0x2909 for
    /// the Witch), u16 action +0x11a (sent only when >= 0), u16 action +0x122. The client does not move until the server
    /// echoes the action (0x1ab).
    /// </summary>
    public sealed record UseSkillAtLocation(int X, int Y, ushort SkillId, ushort Slot, ushort Flags);

    public static UseSkillAtLocation ReadUseSkillAtLocation(ReadOnlySpan<byte> m)
    {
        var r = new LoginReader(m[2..]);
        return new((int)r.U32(), (int)r.U32(), r.U16(), r.U16(), r.U16());
    }

    /// <summary>
    /// S→C 0x1ab (world group, class 0x1435ccc18, read 0x141fe4290; Actor handler 0x14219aa10): an object starts an
    /// action. Object header (u16 id, u32 object id, u32 +0x14, u16 +0x18), u16 flags (+0x38 → action +0x122; bit 0x80 adds
    /// a u32 target object id), compact ints: the actor's position when the action starts (+0x2c/+0x30 → action +0x158)
    /// and the target (+0x24/+0x28 → action +0x130, where the client's own 0x25 keeps its target), u16 skill id (+0x34),
    /// u16 sequence (+0x36 → action +0x11a). For a predicted local actor (sequence ≥ 0) the handler only compares the
    /// position pair with where the client thinks the actor is and teleports it there when they are 92+ grid cells apart
    /// (live 2026-10-16: sending the target in that pair teleported the player on long clicks, onto unwalkable cells).
    /// Otherwise it looks the skill up in the actor's granted skills and starts the action (0x141ef0050 → 0x141e937b0).
    /// </summary>
    /// <summary>S→C 0x1ac "action ended" (read 0x141f988f0): the world object header only (u32 object id, u32 0, u16 0);
    /// the object's current action stops. Ignored for the local player's predicted actions (07 §5).</summary>
    public static byte[] ActionEnded(uint objectId) => new LoginWriter().U16(0x1ac).U32(objectId).U32(0).U16(0).ToArray();

    public static byte[] ActionStarted(uint objectId, int posX, int posY, int targetX, int targetY, ushort skillId, ushort sequence, ushort flags,
        uint? targetObjectId = null, ushort header = 0)
    {
        // Flags bit 0x80: a u32 target object id follows the flags (an action aimed at an object, like C→S 0x24).
        var w = new LoginWriter().U16(ActionStartedId).U32(objectId).U32(0).U16(header)
            .U16((ushort)(targetObjectId is null ? flags & 0xff7f : flags | 0x80));
        if (targetObjectId is { } t) w.U32(t);
        CompactInt(w, posX); CompactInt(w, posY); CompactInt(w, targetX); CompactInt(w, targetY);
        return w.U16(skillId).U16(sequence).ToArray();
    }

    /// <summary>
    /// A server-driven action that starts now: 0x1ab, then 0x1ac in the same tick, as GGG sends them. A server-created
    /// action that paths waits at its target for that 0x1ac (0x141e9afc0, Actor flag 0x10); one still walking swings when
    /// it gets there.
    /// </summary>
    public static byte[] ActionStartedNow(uint objectId, int posX, int posY, int targetX, int targetY, ushort skillId, ushort sequence,
        ushort flags, uint targetObjectId) =>
        [.. ActionStarted(objectId, posX, posY, targetX, targetY, skillId, sequence, flags, targetObjectId), .. ActionEnded(objectId)];

    public const ushort ActionTargetId = 0x1ad;

    /// <summary>
    /// S→C 0x1ad (class 0x1435ccca8, read 0x141fe4640; Actor handler 0x14219aa10): a new target for the action in
    /// progress. Object header, u16 action sequence (+0x20; must match the action's +0x11a), i32 x, i32 y (+0x24),
    /// big-endian. The Actor passes the cell to the action (vtable +0x110), which walks there instead: the held-mouse
    /// update of a lockstep walk (the answer to C→S 0x0192). Ignored for a predicted local action.
    /// </summary>
    public static byte[] ActionTarget(uint objectId, ushort sequence, int x, int y) =>
        new LoginWriter().U16(ActionTargetId).U32(objectId).U32(0).U16(0).U16(sequence).U32((uint)x).U32((uint)y).ToArray();

    public const ushort ActionHitId = 0x1af;

    /// <summary>
    /// S→C 0x1af "action hit" (class 0x1435e8558, read 0x141fe43c0 → 0x1419c2100; Actor handler 0x14219aa10 → 0x141e977f0):
    /// the hit result of an action. The object header names the attacker; then u16 action sequence (+0x40, must match the
    /// action's +0x11a), u8 (+0x43), u8 skill index (+0x42, 0 = the actor's current action), u32 target object id (+0x44),
    /// u16 flags (+0x36), u8 kind bits (bit 0: the message belongs to an action, bit 1: the hit data follows, bits 2/3: a
    /// further u16 each), then u8 (+0x39) and u8 hit angle (+0x3a: 0..255 = a full turn). The handler looks the action up,
    /// picks the damage stat container (or the one the action's sequence names) and calls the attacker Actor's
    /// ProcessDamagePackageInternal (vtable +0x230, 0x141e97e20): the client applies the hit locally, which is what plays
    /// the blood particles and the weapon impact sound ("bonk") on the target. Server life (0x1b1) stays authoritative.
    /// </summary>
    public static byte[] ActionHit(uint objectId, ushort sequence, uint targetObjectId, byte actionByte = 0,
        byte skillIndex = 0, ushort flags = 0, byte kind = 0x03, byte code = 0, byte angle = 0, ushort u34 = 0,
        ushort u3c = 0, ushort u3e = 0)
    {
        var w = new LoginWriter().U16(ActionHitId).U32(objectId).U32(0).U16(0)
            .U16(sequence).U8(actionByte).U8(skillIndex).U32(targetObjectId).U16(flags).U8(kind);
        if ((kind & 0x02) != 0)
        {
            if ((flags & 0x400) != 0) w.U16(u34);
            w.U8(code).U8(angle);
            if ((kind & 0x04) != 0) w.U16(u3c);
            if ((kind & 0x08) != 0) w.U16(u3e);
        }
        return w.ToArray();
    }

    public const ushort InstantActionId = 0x1ce;

    /// <summary>
    /// S→C 0x1ce (class 0x1435ccd40, read 0x141fe4970; Actor handler 0x14219aa10 → 0x14219c1f0): an instant or triggered
    /// action from the server, the answer to C→S 0x191. Object header, u16 skill id (+0x20), u16 action sequence (+0x22 →
    /// action +0x11a; 0xffff = none), u16 flags (+0x38 → action +0x122; 0x20 adds a u32 target object id +0x24, 0x2000 two
    /// compact ints +0x30/+0x34, 0x10 a further struct; a negative low byte makes it a triggered action), then the target
    /// position as compact ints (+0x28/+0x2c), big-endian. The client creates the skill's action, runs it without
    /// animation and consumes a use of the skill: that starts its cooldown on the skill bar (live 2026-10-21).
    /// </summary>
    public static byte[] InstantAction(uint objectId, ushort skillId, int targetX, int targetY, uint? targetObjectId = null,
        ushort sequence = 0xffff)
    {
        var w = new LoginWriter().U16(InstantActionId).U32(objectId).U32(0).U16(0)
            .U16(skillId).U16(sequence).U16((ushort)(targetObjectId is null ? 0 : 0x20));
        if (targetObjectId is { } t) w.U32(t);
        CompactInt(w, targetX); CompactInt(w, targetY);
        return w.ToArray();
    }

    public const ushort LifeUpdateId = 0x1b1;

    /// <summary>
    /// S→C 0x1b1 (world group, class 0x1435e8258, read 0x141fe6fb0; world handler 0x14218ddf0 → the object's Life
    /// component): object header, u32 current (+0x20), u32 reserved (+0x24), u8 pool (+0x28: 0 life, 1 mana, 2 energy
    /// shield), u16 (+0x2a → pool +0x14, probably reserved percent), u8 (+0x2c), all big-endian. 0x141e70550 stores the
    /// values in the pool. For the life pool the handler also calls the Life component's death slot (vtable +0xe8, with
    /// the +0x2c byte) when life drops from ≥ 1 to &lt; 1, and its revive slot (+0x100) on the way back.
    /// </summary>
    public static byte[] LifeUpdate(uint objectId, uint current, uint reserved = 0, byte pool = 0, ushort u2a = 0, byte u2c = 0) =>
        new LoginWriter().U16(LifeUpdateId).U32(objectId).U32(0).U16(0).U32(current).U32(reserved).U8(pool).U16(u2a).U8(u2c).ToArray();

    public const ushort AnimationUpdateId = 0x1b2;

    public const ushort ActorAnimationId = 0x1d1;

    /// <summary>S→C 0x1d1: object header, BE float duration, Animation.dat row (not a rig clip index), bool.
    /// Actor handler 0x14219b333 calls 0x141e9c700, which cancels predictive actions, selects the current stance's
    /// animation and blocks action starts until recovery ends. Row 5 is TakeHit. The optional bool enables the
    /// client's avoid-interruption check; the server normally decides eligibility itself.</summary>
    public static byte[] ActorAnimation(uint objectId, float seconds, byte animationType, bool checkInterruption = false) =>
        new LoginWriter().U16(ActorAnimationId).U32(objectId).U32(0).U16(0)
            .U32(BitConverter.SingleToUInt32Bits(seconds)).U8(animationType).U8(checkInterruption ? (byte)1 : (byte)0).ToArray();

    /// <summary>
    /// S→C 0x1b2 "InstanceClientAnimationUpdate" (world group; the handler maps it and 0x26f/0x270/0x271 to the
    /// Animated component's <c>0x141e5d440</c>): object header, u8 kind, then per kind — 0/1: u32 animation index
    /// (+0x24) and u32 float bits (+0x28), 2: u32 float bits (+0x2c), 3: u32 float bits (+0x28). Kind 0 calls
    /// <c>0x141dbdbc0</c> ("play animation"), which bails when the index is past the actor's animation count, so a
    /// wrong index is ignored rather than fatal (live 2026-10-25).
    /// </summary>
    public static byte[] AnimationUpdate(uint objectId, byte kind, uint index, float speed) =>
        new LoginWriter().U16(AnimationUpdateId).U32(objectId).U32(0).U16(0).U8(kind).U32(index)
            .U32(BitConverter.SingleToUInt32Bits(speed)).ToArray();

    /// <summary>The fixed-height marker of 0x1b7 and 0x1ba that means none: the object follows the terrain.</summary>
    public const uint FollowTerrain = 0x7fffffff;

    public const ushort TeleportId = 0x1b7, MoveToId = 0x1ba, EnterWakeZoneId = 0x1a9, LeaveWakeZoneId = 0x1aa;

    /// <summary>
    /// S→C 0x1a9 (world group, class 0x1435e8238, header only): the object entered the wake zone. The world handler sets
    /// object +0x8d |= 2 and calls 0x141ec7350 ("ObjectInWakeZone"), which adds it to the set of objects that update and
    /// animate. Without it an object exists (position, components, camera follow, teleports work) but is never drawn and
    /// its actions and movement never advance. 0x1aa is the reverse.
    /// </summary>
    public static byte[] EnterWakeZone(uint objectId) => new LoginWriter().U16(EnterWakeZoneId).U32(objectId).U32(0).U16(0).ToArray();

    public static byte[] LeaveWakeZone(uint objectId) => new LoginWriter().U16(LeaveWakeZoneId).U32(objectId).U32(0).U16(0).ToArray();

    public const ushort RemoveObjectId = 0x2bd;

    /// <summary>S→C 0x2bd (world group, header only, read 0x141f988f0): remove an object (the world handler's "remove
    /// object" case, Agents/docs/protocol/07 §4).</summary>
    public static byte[] RemoveObject(uint objectId) => new LoginWriter().U16(RemoveObjectId).U32(objectId).U32(0).U16(0).ToArray();

    /// <summary>
    /// S→C 0x1ba (world group, class 0x1435e85f8, read 0x141fe7810; Positioned handler 0x141e33fd0): move an object to a
    /// grid cell. Object header, i32 x, i32 y, u32 height marker (+0x28: 0x7fffffff = follow the terrain;
    /// otherwise signed i32 marker = -world z / 7.8125). The native endpoint is z = -marker * 7.8125, so positive
    /// markers are above sea level and negative ones below. This is the Positioned blob's i32, not the opposite-sign
    /// ProjectileObject.Height API argument. The float is duration
    /// (+0x2c, seconds: the handler passes distance / duration as the speed to 0x141e37460; Positioned +0x23c = that
    /// speed, +0x240 = the time per cell step = duration / the path's longest axis in cells. Live 2026-10-18: sending
    /// 95.7 meant "~1 s per cell" and the fireball crawled), u8 (+0x30). 0x1b7 (teleport) is the same header, x, y, u32
    /// +0x28 (→ +0x290), u32 +0x2c (→ +0x2b4).
    /// </summary>
    public static byte[] MoveTo(uint objectId, int x, int y, float speed, uint u28 = 0, byte u30 = 0) =>
        new LoginWriter().U16(MoveToId).U32(objectId).U32(0).U16(0)
            .U32((uint)x).U32((uint)y).U32(u28).U32(BitConverter.SingleToUInt32Bits(speed)).U8(u30).ToArray();

    /// <summary>S→C 0x1b9: Positioned orientation in radians, then the orientation-lock flag.
    /// Writer 0x141fe5b90; Positioned 0x141e33fd0 calls SetOrientation (0x141e35920).</summary>
    public static byte[] Orientation(uint objectId, float radians, bool locked = false) =>
        new LoginWriter().U16(0x1b9).U32(objectId).U32(0).U16(0)
            .U32(BitConverter.SingleToUInt32Bits(radians)).U8(locked ? (byte)1 : (byte)0).ToArray();

    public const ushort DeathScreenId = 0x55, ResurrectId = 0x54;

    /// <summary>
    /// S→C 0x55 (instance group, class 0x1435e68a8, read 0x1421a6190, dispatcher case 0x14 → listeners at +0xf78): the
    /// death screen. u8 flags: bit0, bit1 = hide, bit2, bit3, bit4; unless hidden: u32 BE (+0x18), a u8 when that u32 is
    /// non-zero, u16 BE. The HUD (0x140d5adf0 → HUD slot +0x4d0 = 0x140d838f0 → panel 0x140290db0(bit0, bit2, bit3))
    /// shows the "death_screen" panel (title ClientStrings "You have died!"). Its left button: "Exit" with bit0 (hardcore:
    /// it sends C→S 0x153, exit to character selection), "Resurrect in Town" with bit3, else "Resurrect" (shown when no
    /// flag is set); its right button "Resurrect at Checkpoint" with bit2 (without bit0); a third "Spectate" (PvP).
    /// Live 2026-10-17: 0x04 showed only "Resurrect at Checkpoint".
    /// </summary>
    public static byte[] DeathScreen(byte flags)
    {
        var w = new LoginWriter().U16(DeathScreenId).U8(flags);
        if ((flags & 2) == 0) w.U32(0).U16(0);
        return w.ToArray();
    }

    /// <summary>C→S 0x153 (id only): Esc → "Exit to Character Selection" (0x140cf1a20) and the hardcore death screen's
    /// Exit (0x140291180); the client skips it while its instance connection is already down (state +0xc3a0 = 1). The
    /// server answers by closing the instance connection. Disconnected in game (instance at app+0x348), the client's
    /// frame update 0x140d551f0 empties the state stack and pushes the login state (id 7, 0x140d0c8f0) without deleting
    /// the login object; that state's enter (0x140d0d1c0) sees the login connection still logged in (login+0xde8 = 3)
    /// and pushes character select (id 9, 0x140d12170), which asks for the list again with login 0x18.</summary>
    public const ushort ExitToCharacterSelectId = 0x153;

    /// <summary>C→S 0x54 (class 0x1435ca9d8, write 0x141f99ba0), sent by the death screen's buttons (0x140291180):
    /// u8 flags, bit0 set by the "Resurrect in Town" button, clear for the checkpoint one.</summary>
    public static bool ReadResurrectInTown(ReadOnlySpan<byte> m) => (m[2] & 1) != 0;

    /// <summary>Relocate and stop a glide. Unlike MoveTo, height is Positioned's signed fixed-height marker and
    /// worldZ is its float z, copied directly by the client. Marker 0x7fffffff (<see cref="FollowTerrain"/>, the default)
    /// selects terrain and ignores worldZ; any other marker pins the object at that world z. The default was once 0,
    /// which pinned every teleported player at z = 0: under the ground wherever the ground is raised (/tp, arena gates,
    /// Resurrect at Checkpoint).</summary>
    public static byte[] Teleport(uint objectId, int x, int y, uint height = FollowTerrain, float worldZ = 0) =>
        new LoginWriter().U16(TeleportId).U32(objectId).U32(0).U16(0).U32((uint)x).U32((uint)y)
            .U32(height).U32(BitConverter.SingleToUInt32Bits(worldZ)).ToArray();

    /// <summary>S→C 0x128 (class 0x1435e7680, read 0x1421ace20): where objects are, in grid cells: compact count, then
    /// u32 object, u32 x, u32 y. The client (case 0x50 of 0x142150ae0, not in lockstep) puts the player on the first record
    /// when it is 20 or more cells from it, and then puts every listed object on its record; otherwise only the objects
    /// 20 or more cells from theirs. It logs "Resyncing id" for each. The first record must be the player.</summary>
    public const ushort ResyncId = 0x128;
    public static byte[] Resync(IReadOnlyCollection<(uint Id, int X, int Y)> objects)
    {
        var w = new LoginWriter().U16(ResyncId);
        CompactInt(w, objects.Count);
        foreach (var (id, x, y) in objects) w.U32(id).U32((uint)x).U32((uint)y);
        return w.ToArray();
    }

    /// <summary>S→C 0x2b2 (Positioned): object header, u8, u8; two zeros stop the object blocking paths. GGG sends it about
    /// 165 ms after a monster's life reaches 0, with the object id repeated in the header's order key.</summary>
    public static byte[] StopBlocking(uint objectId) =>
        new LoginWriter().U16(0x2b2).U32(objectId).U32(objectId).U16(0x7000).U8(0).U8(0).ToArray();

    /// <summary>The network's signed compact int (writer 0x141faa680, reader 0x141faa790): 1 byte for -0x40..0x3f, then
    /// 2/3/4 bytes with prefixes 0x80/0xc0/0xe0 (6/5/4 value bits in the first byte), else 0xf0 + 4 bytes, big-endian.</summary>
    public static void CompactInt(LoginWriter w, int v)
    {
        uint u = (uint)v;
        if (v >= -0x40 && v < 0x40) { w.U8((byte)(u & 0x7f)); return; }
        if (v >= -0x2000 && v < 0x2000) { w.U8((byte)(((u >> 8) & 0x3f) | 0x80)).U8((byte)u); return; }
        if (v >= -0x100000 && v < 0x100000) { w.U8((byte)(((u >> 16) & 0x1f) | 0xc0)).U8((byte)(u >> 8)).U8((byte)u); return; }
        if (v >= -0x4000000 && v < 0x4000000) { w.U8((byte)(((u >> 24) & 0x0f) | 0xe0)).U8((byte)(u >> 16)).U8((byte)(u >> 8)).U8((byte)u); return; }
        w.U8(0xf0).U32(u);
    }

    /// <summary>Unsigned compact int (client reader 0x14089dc40, in-memory 0x141e655b0): &lt; 0x80 one byte; 0x80 + 14 bits;
    /// 0xc0 + 21 bits; 0xe0 + 28 bits; else 0xf0 + u32 big-endian.</summary>
    public static void CompactUInt(LoginWriter w, uint v)
    {
        if (v < 0x80) { w.U8((byte)v); return; }
        if (v < 0x4000) { w.U8((byte)(0x80 | v >> 8)).U8((byte)v); return; }
        if (v < 0x200000) { w.U8((byte)(0xc0 | v >> 16)).U8((byte)(v >> 8)).U8((byte)v); return; }
        if (v < 0x10000000) { w.U8((byte)(0xe0 | v >> 24)).U8((byte)(v >> 16)).U8((byte)(v >> 8)).U8((byte)v); return; }
        w.U8(0xf0).U32(v);
    }

    /// <summary>
    /// Splits decrypted C→S bytes into messages of known length (fixed, or parsed for 0x15 chat). Stops at the first unknown id (there is no length
    /// prefix, so the rest cannot be framed) and returns it as <paramref name="unknown"/>; a known message cut off at the
    /// end is returned as <paramref name="partial"/> to prepend to the next chunk.
    /// </summary>
    public static List<(ushort Id, byte[] Bytes)> SplitClientMessages(ReadOnlySpan<byte> data, out byte[] partial, out byte[] unknown)
    {
        var result = new List<(ushort, byte[])>();
        partial = []; unknown = [];
        int pos = 0;
        while (pos < data.Length)
        {
            if (data.Length - pos < 2) { partial = data[pos..].ToArray(); break; }
            ushort id = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            int? known = id == InstanceDelve.RouteId ? InstanceDelve.RouteLength(data[pos..]) ?? int.MaxValue
                : id == InstanceChat.ChatId ? InstanceChat.Length(data[pos..]) ?? int.MaxValue
                : id == InstanceItems.CommandId ? InstanceItems.CommandLength(data[pos..]) ?? int.MaxValue
                : id == InstanceNpcs.BuyId ? InstanceNpcs.BuyLength(data[pos..]) ?? int.MaxValue
                : id == InstanceStash.ReorderTabsId ? InstanceStash.ReorderTabsLength(data[pos..]) ?? int.MaxValue
                : id == InstanceStash.RenameTabId ? InstanceStash.RenameTabLength(data[pos..]) ?? int.MaxValue
                : id == InstanceStash.ColourTabId ? InstanceStash.ColourTabLength
                : id == InstanceStash.MoveToAffinityTabId ? InstanceStash.MoveToAffinityTabLength(data[pos..]) ?? int.MaxValue
                : id == InstanceStash.SetAffinitiesId ? InstanceStash.SetAffinitiesLength(data[pos..]) ?? int.MaxValue
                : id == InstanceNpcs.CtrlBuyId ? InstanceNpcs.CtrlBuyLength(data[pos..]) ?? int.MaxValue
                : id is InstanceWaypoints.VisitHideoutId or InstanceWaypoints.VisitMenagerieId ? InstanceWaypoints.VisitHideoutLength(data[pos..]) ?? int.MaxValue
                : id is InstanceSocial.PartyInviteId or InstanceSocial.PartyKickId ? InstanceSocial.PartyInviteLength(data[pos..]) ?? int.MaxValue
                : id == InstanceTransfer.LeaveAreaAckId ? InstanceTransfer.LeaveAreaAckLength(data[pos..]) ?? int.MaxValue
                : id == InstanceSocial.FriendAddId ? InstanceSocial.FriendAddLength(data[pos..]) ?? int.MaxValue
                : id == InstanceSocial.StatusMessageId ? InstanceSocial.StatusMessageLength(data[pos..]) ?? int.MaxValue
                : id == InstanceSocial.PartyDescribeId ? InstanceSocial.PartyDescribeLength(data[pos..]) ?? int.MaxValue : KnownClientLength(id);
            if (known is not int len) { unknown = data[pos..].ToArray(); break; }
            if (data.Length - pos < len) { partial = data[pos..].ToArray(); break; }
            result.Add((id, data.Slice(pos, len).ToArray()));
            pos += len;
        }
        return result;
    }

    /// <summary>Parses the client's plaintext opening, or null if fewer than <see cref="OpeningLength"/> bytes arrived.</summary>
    public static InstanceOpening? TryReadOpening(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < OpeningLength) return null;
        var r = new LoginReader(buf);
        if (r.U16() != TicketId) throw new InvalidDataException("instance connection must start with 0x000F");
        uint ticket = r.U32();
        if (r.U16() != HelloId) throw new InvalidDataException("expected 0x000E after 0x000F");
        return new InstanceOpening(ticket, r.Fixed(12), r.U32(), r.U32(), r.U8(), r.U8());
    }
}

/// <summary>
/// S→C 0x1d (class 0x1435e4c20, write 0x142147f10; dispatcher case 6): the instance's area. The client logs
/// "Client-Safe Instance ID = {InstanceId}" and "Generating level {Level} area "{id}" with seed {Seed}", then queues area
/// generation. AreaHash is WorldAreas.HASH16 (unknown: "Instance sent world area that does not exist"). Level 0 means
/// the area's own level. LeagueStats enables flag 0x21.6 and the first compact pair list (+0x100), copied to world
/// stats at +0x120 by 0x142189aa0. Other optional parts remain off. Defaults follow the constructor (0x1421a42b0).
/// </summary>
public sealed record AreaInstance(uint InstanceId, ushort AreaHash, uint Seed, byte Level, string Text = "",
    byte B1e = 3, byte B20 = 0, byte B1f = 2, byte B24 = 0x23, byte B1d = 0, byte B28 = 0x3c,
    IReadOnlyDictionary<uint, int>? LeagueStats = null, byte[]? TerrainData = null,
    IReadOnlyList<byte>? AchievementSets = null)
{
    public byte[] Encode()
    {
        bool hasStats = LeagueStats is { Count: > 0 };
        var writer = new LoginWriter()
            .U16(InstanceProtocol.AreaInstanceId).U32(InstanceId).U16(AreaHash).WStr(Text).U32(Seed)
            .U8(B1e).U8(B20).U8(B1f).U8(B24).Bytes([(byte)(hasStats ? 0x40 : 0), (byte)(TerrainData is null ? 0 : 4), 0]).U8(Level).U8(B1d).U8(B28)
            .U16(0)  // vec16<u32> +0x68
            .U16(0); // vec16<u8> +0x80
        // +0x50 becomes instance +0xc418. The shared achievements/league-upgrades TabControl
        // stays hidden when this list is empty, even when the Delve child tab exists.
        if (AchievementSets is { Count: > byte.MaxValue }) throw new ArgumentOutOfRangeException(nameof(AchievementSets));
        writer.U8((byte)(AchievementSets?.Count ?? 0));
        foreach (byte set in AchievementSets ?? []) writer.U8(set);
        writer.U16(0); // vec16<u16> +0xe0
        if (hasStats)
        {
            InstanceProtocol.CompactUInt(writer, (uint)LeagueStats!.Count);
            foreach (var (key, value) in LeagueStats.OrderBy(p => p.Key))
            {
                InstanceProtocol.CompactUInt(writer, key);
                InstanceProtocol.CompactInt(writer, value);
            }
            InstanceProtocol.CompactUInt(writer, 0); // second compact pair list (+0x120)
            writer.U8(0); // third list (+0x138), u8 count, no entries
        }
        if (TerrainData is { } terrain)
        {
            if (terrain.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(TerrainData));
            writer.U16((ushort)terrain.Length).Bytes(terrain);
        }
        return writer.ToArray();
    }
}

/// <summary>
/// C→S 0x0F + 0x0E. Ticket is login 0x14's u32 at +0x38. Digest: 12 bytes the client computes over a constant 8 bytes,
/// 0x14's Data8 and the u32 _time64 (0x141fe7d90 family, algorithm not identified yet). Address is 0x14's u32 at +0x34.
/// U32_128 is always 0, Realm is 0x40 (international) and Flag is global 0x1445998ce.
/// </summary>
public sealed record InstanceOpening(uint Ticket, byte[] Digest, uint Address, uint U32_128, byte Realm, byte Flag);

/// <summary>
/// The 64-byte instance key from a ticket. Login and instance servers run as separate processes, so instead of passing
/// keys between them both derive key = HMAC-SHA512(secret, ticket BE) from a shared secret.
/// </summary>
public sealed class InstanceKeyring(byte[] secret)
{
    public static InstanceKeyring Random() => new(RandomNumberGenerator.GetBytes(64));

    /// <summary>Loads the secret from <paramref name="path"/>, creating it on first use.</summary>
    public static InstanceKeyring LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try
            {
                using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                f.Write(RandomNumberGenerator.GetBytes(64));
            }
            catch (IOException) when (File.Exists(path)) { } // the other server created it first
        }
        return new(File.ReadAllBytes(path));
    }

    public uint NewTicket() => (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);

    public byte[] Key(uint ticket)
    {
        Span<byte> t = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(t, ticket);
        return HMACSHA512.HashData(secret, t);
    }

    /// <summary>The server's Salsa20 pair for a ticket (mirror of the client's isClient=1 setup).</summary>
    public ConnectionCiphers Ciphers(uint ticket) => new(Key(ticket), isClient: false);
}
