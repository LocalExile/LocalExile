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

    public const ushort AddRulesetId = 0x1f;

    /// <summary>S→C 0x1f: u8 Rulesets row, u32 (big-endian, unused by the factories). Unless the instance already holds a
    /// ruleset of that row, the client builds it from its factory table and adds it to the instance's ruleset list
    /// (instance dispatcher 0x142150ae0 case 8, slot 8 0x1415a6800); client code finds a ruleset there by its row.</summary>
    public static byte[] AddRuleset(byte row, uint value = 0) => new LoginWriter().U16(AddRulesetId).U8(row).U32(value).ToArray();

    /// <summary>0x1d's extra data (<see cref="AreaInstance.TerrainData"/>) holding only terrain plugin states: flag bytes
    /// 0x00, 0x01 (second byte bit 0), u8 count, then per plugin its TerrainPlugins <c>__col1</c> id (u16), the state's
    /// length (u16) and the state, little-endian (<c>0x141ecdad0</c> → world +0x70). At generation start the client
    /// creates each listed plugin with its state, then the area's own plugin with an empty one unless listed
    /// (<c>0x141eca930</c>).</summary>
    public static byte[] TerrainPluginStates(IReadOnlyList<(ushort Plugin, byte[] State)> states)
    {
        if (states.Count is 0 or > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(states));
        var bytes = new List<byte> { 0x00, 0x01, (byte)states.Count };
        foreach (var (plugin, state) in states)
        {
            if (state.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(states));
            bytes.AddRange([(byte)plugin, (byte)(plugin >> 8), (byte)state.Length, (byte)(state.Length >> 8)]);
            bytes.AddRange(state);
        }
        return [.. bytes];
    }

    /// <summary>
    /// S→C 0x21 (class 0x1435e65c8, write 0x1421a9710; dispatcher case 10): enter the area. Two stash tab lists
    /// (<see cref="InstanceStash.WriteTabs"/>: the personal stash → instance +0xc498, the trade tabs → +0xc4c8), vec16 of
    /// (u32, u32), wstr, 24 flag bits, u8 +0x7e (world frame length in ms, passed to <c>0x141648b50</c> → world +0x11c),
    /// u8 bits (+0x7b/+0x7c/+0x7d), the rest empty or zero. Its handler (0x14215d6d0) needs the local player entity
    /// (0x22 + 0x2bb) to exist already.
    /// </summary>
    /// <remarks>Flag bit 0 (+0x78) is the active weapon set: set = the second (Weapon2 / Offhand2). The handler copies it
    /// to the session's +0xcd31 (the dispatcher's r13 is the session + 8, so its "[r13+0xcd29]"), as S→C 0xde does.</remarks>
    public static byte[] EnterArea(IReadOnlyList<InstanceStash.Tab>? stash = null, bool secondWeaponSet = false, byte frameMs = 0)
    {
        var w = new LoginWriter().U16(EnterAreaId);
        InstanceStash.WriteTabs(w, stash ?? []);
        return w.U16(0).U16(0).WStr("").Bytes([(byte)(secondWeaponSet ? 0x01 : 0), 0, 0]).U8(frameMs).U8(0).ToArray();
    }

    /// <summary>C→S 0xdd (3 bytes, built by 0x14216faa0): swap the weapon set. u8: the set wanted (1 the second, 0 the
    /// first; the client sends "current set is the first"). It then waits (session +0xcfa4 = 1) for S→C 0xde.</summary>
    public const ushort SwapWeaponsId = 0xdd;

    /// <summary>C→S 0x193 (6 bytes, class 0x1431767e8, writer 0x140911210, sender 0x14011ecb0): retarget an active
    /// held/channelled action onto a target object (u32 BE target object id). Sent in both lockstep and predictive modes.</summary>
    public const ushort RetargetActionId = 0x0193;
    public const int RetargetActionLength = 6;

    public static uint ReadRetargetAction(ReadOnlySpan<byte> m) =>
        BinaryPrimitives.ReadUInt32BigEndian(m[2..]);

    /// <summary>C→S 0x197 (10 bytes, live 2026-10-23 while Cyclone was held) and C→S 0x198 (the same 10 bytes, class and
    /// handler, which a channelled spell that stands still — Winter Orb — sends instead, live 2026-11-09): a channelled
    /// skill goes on, u32 BE x, u32 BE y (grid cells) of the point it is aimed at now (the cursor, not the player). C→S
    /// 0x199 (2 bytes): the channel ends (the button let go). Read from captures.</summary>
    public const ushort ChannelUpdateId = 0x197, ChannelUpdateAltId = 0x198, ChannelEndId = 0x199;

    /// <summary>The channel update in either form the client sends it.</summary>
    public static bool IsChannelUpdate(ushort id) => id is ChannelUpdateId or ChannelUpdateAltId;

    public static (int X, int Y) ReadChannelUpdate(ReadOnlySpan<byte> m) =>
        ((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[2..]), (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(m[6..]));

    /// <summary>S→C 0xde (3 bytes, class 0x14346cd80, dispatcher case 0x42 at 0x1421554b5): the weapon set. u8 flags: bit 0
    /// the active set (→ session +0xcd31; a change tells the listeners at +0x72f8), bit 1 keeps the client waiting
    /// (session +0xcfa4 = 2), else the wait ends (0). Verified with a live trace, 2026-10-22.</summary>
    public const ushort WeaponSetId = 0xde;

    public static byte[] WeaponSet(bool second) => new LoginWriter().U16(WeaponSetId).U8((byte)(second ? 1 : 0)).ToArray();

    public const ushort TickId = 0x1b, PongId = 0x1c, AreaGeneratedId = 0x7d;

    /// <summary>C→S 0x7d (10 bytes, class 0x1435ca2c0, sent by tick 0x14214f1d0 when generation finishes): u32 BE
    /// tile hash (<c>0x142054070</c>) and u32 BE doodad hash (<c>0x142048e20</c>) from world <c>+0x114</c>. Live
    /// capture (1_1_town seed 1): <c>007D 43E6886D 8EA8DD43</c> (TileHash 1139181677, DoodadHash 2393431363).</summary>
    public static (uint TileHash, uint DoodadHash) ReadAreaGenerated(ReadOnlySpan<byte> m) =>
        (BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]));

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
        InstanceDivination.TurnInId => InstanceDivination.TurnInLength,
        InstanceDivination.MoveId => InstanceDivination.MoveLength,
        InstanceDelve.UpgradesRequestId => 2, InstanceDelve.ChartRequestId => 2, InstanceDelve.UpgradeId => 4, InstanceDelve.PurchaseId => 4,
        InstanceMonsterStatue.CreateId => InstanceMonsterStatue.CreateLength,
        InstanceItems.CurrencyApplicationForesightRequestId => InstanceItems.CurrencyApplicationForesightRequestLength,
        InstanceStash.ColourTabId => InstanceStash.ColourTabLength,
        TickId => 7, AreaGeneratedId => 10, UseSkillAtLocationId => 16, 0x015a => 2, 0x01ef => 2, 0x0099 => 4, 0x0129 => 2,
        InstanceChat.LinkItemId => InstanceChat.LinkItemLength, InstanceChat.LinkOtherId => InstanceChat.LinkOtherLength,
        InstanceChat.LinkAnotherId => InstanceChat.LinkAnotherLength,
        TutorialCompleteId => TutorialCompleteLength,
        // Primary 3.29.3c writer: 0x141f99e90 (u16 + 4/2/2/4/1/1 bytes).
        0x0059 => 16, InstanceSocial.TradeRequestId => InstanceSocial.TradeRequestLength,
        InstanceSocial.TradeRequestAcceptId => InstanceSocial.TradeRequestAcceptLength,
        InstanceSocial.PartyAcceptId => InstanceSocial.PartyAcceptLength,
        InstanceSocial.PartyLeaveId => InstanceSocial.PartyLeaveLength,
        InstanceChat.JoinChannelId => InstanceChat.JoinChannelLength, InstancePantheon.ChooseGodId => InstancePantheon.ChooseGodLength,
        InstanceWaypoints.VisitDelveHubId => InstanceWaypoints.VisitDelveHubLength, InstanceWaypoints.VisitSanctumId => InstanceWaypoints.VisitSanctumLength,
        InstanceWaypoints.VisitKingsmarchId => InstanceWaypoints.VisitKingsmarchLength, InstanceWaypoints.VisitHeistHubId => InstanceWaypoints.VisitHeistHubLength,
        InstanceWaypoints.VisitChayulaHubId => InstanceWaypoints.VisitChayulaHubLength, InstanceWaypoints.VisitDeepwaterHubId => InstanceWaypoints.VisitDeepwaterHubLength,
        InstanceTransfer.UseSkillOnObjectId => 12, InstanceTransfer.ChooseInstanceId => 3, ResurrectId => 3, InstanceItems.DropItemId => 2,
        InstanceItems.PickUpItemId => 13, InstanceItems.PlaceItemId => 18,
        // Ctrl + click with the map device open: a map sends one of these, never 0xe8 (that is the Atlas Idol path).
        InstanceMapDevice.MoveIntoDeviceId => InstanceMapDevice.MoveItemLength,
        InstanceMapDevice.MoveOutOfDeviceId => InstanceMapDevice.MoveItemLength,
        InstanceItems.MissionSlotMoveId or InstanceItems.CraftingSlotMoveId or InstanceItems.LockerMoveId
            or InstanceItems.ExtractionMoveId or InstanceItems.SocketStorageMoveId => InstanceItems.LeagueWindowMoveLength,
        InstanceItems.CollectionTakeId => InstanceItems.CollectionTakeLength,
        InstanceItems.InventoryRequestId => InstanceItems.InventoryRequestLength, InstanceItems.UseItemId => InstanceItems.UseItemLength, InstanceItems.UseItemOnId => InstanceItems.UseItemOnLength, InstanceItems.UseItemOnObjectId => InstanceItems.UseItemOnObjectLength, InstanceItems.UseItemOnPassiveId => InstanceItems.UseItemOnPassiveLength, InstanceItems.UseFlaskId => InstanceItems.UseFlaskLength, InstanceItems.UseItemTypeOnId => InstanceItems.UseItemTypeOnLength,
        InstanceItems.ItemActionId => InstanceItems.ItemActionLength, InstanceItems.SplitStackId => InstanceItems.SplitStackLength, InstanceItems.SocketGemId => 16,
        InstanceItems.UnsocketGemId => 17, BindSkillId => 5, AttackInPlaceId => 4, SlotMask52Id => 4, ExitToCharacterSelectId => 2,
        InstanceItems.LevelUpGemId => 14, InstanceItems.LevelUpAllGemsId => InstanceItems.LevelUpAllGemsLength, InstanceItems.LockGemId => InstanceItems.LockGemLength,
        InstanceEntities.AllocatePassiveId => 7, InstanceEntities.MasteryEffectId => InstanceEntities.MasteryEffectLength, InstanceEntities.RefundPassiveId => InstanceEntities.RefundPassiveLength,
        InstanceEntities.CancelBuffId => InstanceEntities.CancelBuffLength,
        InstanceNpcs.VoteAnswerId => InstanceNpcs.VoteAnswerLength,
        InstanceEntities.RefundAllPassivesId => 2, InstanceEntities.PanelToggleId => 3, InstanceWaypoints.TravelId => InstanceWaypoints.TravelLength, InstanceNpcs.DialogueClosedId => 2, InstanceNpcs.OptionId => InstanceNpcs.OptionLength, InstanceNpcs.TakeAllRewardsId => InstanceNpcs.TakeAllRewardsLength, InstanceNpcs.TradeAnswerId => InstanceNpcs.TradeAnswerLength, InstanceNpcs.TradeOfferId => InstanceNpcs.TradeOfferLength, InstanceNpcs.TradeTakeBackId => InstanceNpcs.TradeTakeBackLength, InstanceNpcs.TradeMoveAllId => InstanceNpcs.TradeMoveAllLength, InstanceNpcs.TradeAcceptId => InstanceNpcs.TradeAcceptLength, InstanceNpcs.TradeUnacceptId => InstanceNpcs.TradeUnacceptLength, InstanceStash.RequestTabId => InstanceStash.RequestTabLength, InstanceStash.MoveTabId => InstanceStash.MoveTabLength, InstanceStash.DeleteFolderId => InstanceStash.DeleteFolderLength, InstanceStash.MoveToTabId => InstanceStash.MoveToTabLength, InstanceStash.TakeFromTabId => InstanceStash.TakeFromTabLength, InstanceMapDevice.ActivateId => InstanceMapDevice.ActivateLength, InstanceMapDevice.SelectMapId => InstanceMapDevice.SelectMapLength, InstanceMapDevice.MoveId => InstanceMapDevice.MoveLength, InstanceMapDevice.SocketId => InstanceMapDevice.SocketLength, InstanceEntities.AtlasAllocateId => InstanceEntities.AtlasAllocateLength, InstanceEntities.AtlasRefundId => InstanceEntities.AtlasRefundLength,
        InstanceEntities.AtlasTreeTabId or InstanceEntities.AtlasRefundAllId or InstanceEntities.AtlasTreeWidgetId => InstanceEntities.AtlasTreeTabLength,
        SwapWeaponsId => 3, RetargetActionId => RetargetActionLength, ChannelUpdateId => 10, ChannelUpdateAltId => 10, ChannelEndId => 2, LockstepMoveStartId => 15, LockstepMoveUpdateId => 10, LockstepMoveStopId => 2, LockstepUseOnObjectId => 11, LockstepSkillStopId => 4,
        UseInstantSkillAtLocationId => 12, UseInstantSkillId => 8,
        InstancePause.RequestId => InstancePause.RequestLength, InstanceMicrotransactions.SlotId => InstanceMicrotransactions.SlotLength,
        InstanceWaypoints.LabyrinthChosenId => InstanceWaypoints.LabyrinthChosenLength,
        InstanceWaypoints.AscendancyChosenId => InstanceWaypoints.AscendancyChosenLength,
        InstanceWaypoints.DescendancyChosenId => InstanceWaypoints.DescendancyChosenLength,
        InstanceMicrotransactions.SkillPickId => InstanceMicrotransactions.SkillPickLength,
        InstanceMicrotransactions.RequestId => InstanceMicrotransactions.RequestLength,
        InstanceSkinTransfers.BindId => InstanceSkinTransfers.BindLength,
        InstanceSkinTransfers.ReclaimId => InstanceSkinTransfers.ReclaimLength,
        InstanceHideouts.SelectId => InstanceHideouts.SelectLength,
        InstanceHideoutEditing.MoveId => InstanceHideoutEditing.MoveLength,
        InstanceBench.CraftId => InstanceBench.CraftLength, InstanceBench.MoveId => InstanceBench.MoveLength, InstanceHarvest.CraftId => InstanceHarvest.CraftLength,
        InstanceCurrencyExchange.RequestOrdersId => InstanceCurrencyExchange.RequestOrdersLength, InstanceCurrencyExchange.PlaceOrderId => InstanceCurrencyExchange.PlaceOrderLength,
        InstanceCurrencyExchange.CloseOrderId => InstanceCurrencyExchange.CloseOrderLength, InstanceCurrencyExchange.ClaimId => InstanceCurrencyExchange.ClaimLength,
        InstanceCurrencyExchange.RequestMarketId => InstanceCurrencyExchange.RequestMarketLength, InstanceCurrencyExchange.RequestPartnersId => InstanceCurrencyExchange.RequestPartnersLength,
        InstanceCurrencyExchange.RequestFeeId => InstanceCurrencyExchange.RequestFeeLength,
        // Remaining exact fixed-length C→S instance opcodes from 3.29.3c (Agents/docs/protocol/c2s.json), excluding
        // plaintext opening 0x000e/0x000f.
        0x0038 or 0x011a or 0x012e or 0x013c or 0x013e or 0x0159 or 0x0168 or 0x0169 or 0x016a or 0x016c or 0x0171
            or 0x0186 or 0x018a or 0x018d or 0x019b or 0x020e or 0x021e or 0x0224 or 0x0243 or 0x024c or 0x0257
            or 0x025d or 0x0260 or 0x0264 or 0x027e or 0x0296 or 0x02a6 or 0x02ac or 0x02ce or 0x02d5 => 2,
        0x001a or 0x005e or 0x0083 or 0x00ac or 0x00d9 or 0x00da or 0x0156 or 0x0187 or 0x01f7 or 0x01f8
            or 0x0245 or 0x024f or 0x0297 or 0x0298 or 0x02a3 or 0x02a4 => 3,
        0x011d or 0x0142 or 0x0143 or 0x016b or 0x01fd or 0x021d or 0x0221 or 0x0222 or 0x0247 or 0x0255 or 0x02b1
            or 0x02d0 or 0x02d1 => 4,
        0x0249 or 0x027d => 5,
        0x005c or 0x005d or 0x0066 or 0x0069 or 0x00a2 or 0x00d0 or 0x01fc or 0x0206 or 0x020b
            or 0x020f or 0x0217 or 0x0218 or 0x0259 or 0x026b or 0x0275 or 0x027a or 0x0284 or 0x0288 or 0x0289
            or 0x02a1 or 0x02fd => 6,
        0x002e or 0x0065 or 0x0068 or 0x0107 or 0x0152 or 0x017c or 0x017f or 0x0180 or 0x0185 or 0x01da
            or 0x01dc or 0x01e0 or 0x01fb or 0x01fe or 0x0216 or 0x028e or 0x02d6 => 7,
        0x0062 or 0x00ef or 0x00f0 or 0x00f1 or 0x00f2 or 0x00f8 or 0x00fa or 0x00fb or 0x00fc or 0x00ff or 0x0100
            or 0x0102 or 0x0104 or 0x0105 or 0x0108 or 0x0109 or 0x010d or 0x01ec => 8,
        0x00e2 or 0x00f4 or 0x00fd or 0x0106 or 0x010a or 0x010b or 0x010c => 9,
        0x0061 or 0x00f9 or 0x013a or 0x01a7 or 0x01d8 or 0x01d9 or 0x0265 or 0x029b or 0x02d3 => 10,
        0x0057 or 0x0060 or 0x0063 or 0x0064 or 0x0177 => 11,
        0x005f or 0x0075 or 0x00b4 or 0x00f5 or 0x00f7 or 0x00fe or 0x0101 or 0x0110 or 0x02b0 => 12,
        0x002c => 13,
        0x0067 or 0x00cf => 14,
        0x005a or 0x0114 => 16,
        0x0076 or 0x0078 => 17,
        0x0079 or 0x0283 or 0x0294 or 0x0299 => 18,
        0x0089 => 20,
        0x002b => 21,
        0x029f => 25,
        0x0073 or 0x0116 => 26,
        0x029e => 28,
        _ => null,
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

    /// <summary>C→S 0x018f. u8 0 on a free player, 3 for a request made during a swing, which GGG serves when the swing
    /// ends. Flags: 0x0400 on a new press, 0x0001 attack in place (07 §6, "Action flags"), 0x0008 the client's
    /// auto_equip option.</summary>
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

    /// <summary>S→C 0x1ac (read 0x141f988f0): the world object header only. The historical name is not a generic
    /// stop command: Actor 0x14219aa10 decrements its pending-action counter, then in Predictive releases an eligible
    /// server-created action (including its walk-up/swing gate). It does not stop the local player's predicted
    /// nonnegative-sequence action; Lockstep skips the Predictive release branch. See protocol/07 §5.</summary>
    public static byte[] ActionEnded(uint objectId) => new LoginWriter().U16(0x1ac).U32(objectId).U32(0).U16(0).ToArray();

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
    /// <paramref name="orderKey"/> and <paramref name="checkpoint"/> are the header's ordering fields: where in a
    /// lockstep client's frame the message is applied (0 = at the frame's start).
    /// </summary>
    public static byte[] ActionHit(uint objectId, ushort sequence, uint targetObjectId, byte actionByte = 0,
        byte skillIndex = 0, ushort flags = 0, byte kind = 0x03, byte code = 0, byte angle = 0, ushort u34 = 0,
        ushort u3c = 0, ushort u3e = 0, uint orderKey = 0, ushort checkpoint = 0)
    {
        var w = new LoginWriter().U16(ActionHitId).U32(objectId).U32(orderKey).U16(checkpoint)
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
        ushort sequence = 0xffff, ushort flags = 0, bool triggered = false)
    {
        var w = new LoginWriter().U16(InstantActionId).U32(objectId).U32(0).U16(0)
            .U16(skillId).U16(sequence).U16((ushort)(flags | (triggered ? 0x80 : 0) | (targetObjectId is null ? 0 : 0x20)));
        if (targetObjectId is { } t) w.U32(t);
        CompactInt(w, targetX); CompactInt(w, targetY);
        return w.ToArray();
    }

    public const ushort LifeUpdateId = 0x1b1;

    /// <summary>S→C 0x1b1 <c>u2c</c> death kind: 0 plays the ordinary death animation (Animation row 6), while 1, 3 and 4
    /// select <c>DeathFromDegen</c> (Animation row 0x77, <c>0x141e9c200</c>).</summary>
    public const byte DeathKindNormal = 0, DeathKindDegen = 1;

    /// <summary>
    /// S→C 0x1b1 (world group, class 0x1435e8258, read 0x141fe6fb0; world handler 0x14218ddf0 → the object's Life
    /// component): object header, u32 current (+0x20), u32 reserved (+0x24), u8 pool (+0x28: 0 life, 1 mana, 2 energy
    /// shield), u16 (+0x2a → pool +0x14, probably reserved percent), u8 (+0x2c death kind: <see cref="DeathKindNormal"/>
    /// or <see cref="DeathKindDegen"/>), all big-endian. 0x141e70550 stores the values in the pool. For the life pool the
    /// handler also calls the Life component's death slot (vtable +0xe8, with the +0x2c byte) when life drops from ≥ 1 to
    /// &lt; 1, and its revive slot (+0x100) on the way back.
    /// </summary>
    public static byte[] LifeUpdate(uint objectId, uint current, uint reserved = 0, byte pool = 0, ushort u2a = 0, byte u2c = 0,
        uint orderKey = 0, ushort checkpoint = 0) =>
        new LoginWriter().U16(LifeUpdateId).U32(objectId).U32(orderKey).U16(checkpoint).U32(current).U32(reserved).U8(pool).U16(u2a).U8(u2c).ToArray();

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
    /// S→C 0x1b2 "InstanceClientAnimationUpdate" (world group, read <c>0x141fe58a0</c>; the handler maps it and
    /// 0x26f/0x270/0x271 to the Animated component's <c>0x141e5d440</c>): object header (12 B), u8 kind, then per
    /// kind — 0/1: u32 animation index (+0x24) and u32 float bits (+0x28) (21 B total); 2: u32 float bits (+0x2c)
    /// (17 B total); 3: u32 float bits (+0x28) (17 B total); ≥4 throws "Bad packet: InstanceClientAnimationUpdate".
    /// Kind 0 calls <c>0x141dbdbc0</c> ("play animation"), which bails when the index is past the actor's animation
    /// count, so a wrong index is ignored rather than fatal (live 2026-10-25).
    /// </summary>
    public static int AnimationUpdateLength(byte kind) => kind <= 1 ? 21 : kind <= 3 ? 17 : -1;

    public static byte[] AnimationUpdate(uint objectId, byte kind, uint index = 0, float speed = 1f)
    {
        if (kind > 3) throw new ArgumentOutOfRangeException(nameof(kind), kind, "S→C 0x1b2 AnimationUpdate only accepts kinds 0..3");
        var w = new LoginWriter().U16(AnimationUpdateId).U32(objectId).U32(0).U16(0).U8(kind);
        if (kind <= 1) w.U32(index).U32(BitConverter.SingleToUInt32Bits(speed));
        else w.U32(BitConverter.SingleToUInt32Bits(speed));
        return w.ToArray();
    }

    /// <summary>The fixed-height marker of 0x1b7 and 0x1ba that means none: the object follows the terrain.</summary>
    public const uint FollowTerrain = 0x7fffffff;

    public const ushort MonstersRemainingId = 0x1a0;

    /// <summary>
    /// S→C 0x1a0 (instance group, class 0x14346cd80, read 0x140e27980, dispatcher case 0x7e at 0x14215718b): the map
    /// overlay's "X monsters remain" line. One payload byte, <c>byte [msg+0x10]</c>, which the handler copies to
    /// <c>instance+0xd7cd</c> (the dispatcher's <c>r13</c> is the instance + 8: <c>mov byte ptr [r13+0xd7c5]</c>, then
    /// notifies the observers that invalidate the overlay). That byte is the only input of the line and 0x1a0 its only
    /// writer in the client: the instance constructor (0x14214b4a0, at 0x14214e686) starts the word at
    /// <c>instance+0xd7cc</c> at 0xff00, so with nobody sending this the count always read 255, and the text builder
    /// (0x1406b24e0, called by the overlay builder 0x1406b25f0 at 0x1406b279b) picks ClientStrings 0x1ab6
    /// "1 monster remains" for 1, 0x1ab7 "{0} monsters remain" for 0..50 and 0x1ab8 "More than 50 monsters remain" for
    /// 51..255. Nothing on the client counts monsters, so the count only changes when a server sends this again.
    /// Live 2026-11-14 with <c>/raw</c> (an area entered, so the instance exists): <c>01a0 05</c> → "5 monsters remain",
    /// <c>01a0 01</c> → "1 monster remains", <c>01a0 00</c> → "0 monsters remain", <c>01a0 33</c> → "More than 50
    /// monsters remain" (51..255 all read so; 0xff is not special-cased). <paramref name="remaining"/> is clamped to the
    /// byte: 51..255 render alike and a bigger area must be sent as 255, not wrapped.
    /// </summary>
    public static byte[] MonstersRemaining(int remaining) =>
        new LoginWriter().U16(MonstersRemainingId).U8((byte)Math.Min(Math.Max(remaining, 0), byte.MaxValue)).ToArray();

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

    /// <summary>C→S 0x020a (3 bytes, vtable 0x14346cd00, writer 0x140e27920): a tutorial step or Help Panel article
    /// completed or dismissed; payload byte is the Tutorial.dat row index (0..18 tutorials, 17 = AllTutorials,
    /// 23..76 Help Panel articles, 77 = HidePopups).</summary>
    public const ushort TutorialCompleteId = 0x020a;
    public const int TutorialCompleteLength = 3;
    public static byte ReadTutorialComplete(ReadOnlySpan<byte> m) => m[2];

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
    /// Splits decrypted C→S bytes into messages of known length (fixed or variable). Stops at the first unknown id or
    /// malformed variable-length field (there is no length prefix, so the rest cannot be framed) and returns it as
    /// <paramref name="unknown"/>; a known message cut off at the end is returned as <paramref name="partial"/> to
    /// prepend to the next chunk. Never throws on malformed wire fields.
    /// </summary>
    public static List<(ushort Id, byte[] Bytes)> SplitClientMessages(ReadOnlySpan<byte> data, out byte[] partial, out byte[] unknown)
    {
        var result = new List<(ushort, byte[])>();
        partial = []; unknown = [];
        int pos = 0;
        while (pos < data.Length)
        {
            if (data.Length - pos < 2) { partial = data[pos..].ToArray(); break; }
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            var status = TryClientMessageLength(id, data[pos..], out int len);
            if (status == WireMeasure.Invalid) { unknown = data[pos..].ToArray(); break; }
            if (status == WireMeasure.Incomplete || data.Length - pos < len) { partial = data[pos..].ToArray(); break; }
            result.Add((id, data.Slice(pos, len).ToArray()));
            pos += len;
        }
        return result;
    }

    /// <summary>Custom mod message envelope (non-retail extension): u16 BE 0xF7E0, u32 BE payload length
    /// (≤ <see cref="ModEnvelopeMaxPayload"/>), followed by <c>payloadLen</c> bytes. Framed cleanly so subsequent
    /// retail messages in the same TCP read continue decoding and split envelopes reassemble across reads.</summary>
    public const ushort ModEnvelopeId = 0xF7E0;

    /// <summary>Maximum payload length accepted in a <see cref="ModEnvelopeId"/> frame (1 MiB).</summary>
    public const int ModEnvelopeMaxPayload = 1 << 20;

    static WireMeasure TryClientMessageLength(ushort id, ReadOnlySpan<byte> data, out int length)
    {
        switch (id)
        {
            case InstanceDelve.RouteId: return FromNullableLength(InstanceDelve.RouteLength(data), out length);
            case InstanceChat.ChatId: return InstanceChat.MeasureLength(data, out length);
            case InstanceItems.CommandId: return InstanceItems.MeasureCommandLength(data, out length);
            case InstanceNpcs.BuyId: return FromNullableLength(InstanceNpcs.BuyLength(data), out length);
            case InstanceNpcs.CtrlBuyId: return FromNullableLength(InstanceNpcs.CtrlBuyLength(data), out length);
            case InstanceStash.ReorderTabsId: return FromNullableLength(InstanceStash.ReorderTabsLength(data), out length);
            case InstanceStash.RenameTabId: return InstanceStash.MeasureRenameTabLength(data, out length);
            case InstanceStash.MoveToAffinityTabId: return FromNullableLength(InstanceStash.MoveToAffinityTabLength(data), out length);
            case InstanceStash.SetAffinitiesId: return FromNullableLength(InstanceStash.SetAffinitiesLength(data), out length);
            case InstanceWaypoints.VisitHideoutId or InstanceWaypoints.VisitMenagerieId:
                return InstanceWaypoints.MeasureVisitHideoutLength(data, out length);
            case InstanceSocial.FriendAddId: return InstanceSocial.MeasureFriendAddLength(data, out length);
            // 2 + WStr (writer 0x141f98140).
            case InstanceSocial.PartyInviteId or InstanceSocial.PartyKickId or InstanceSocial.PartyTravelId
                or 0x0097 or 0x00ca or 0x0140 or 0x0141 or 0x0145 or 0x014d or 0x014e or 0x014f or 0x0155:
                return MeasureWStrMessage(data, prefixBytes: 2, suffixBytes: 0, out length);
            // 2 + WStr + u8 (writer 0x141f9baf0).
            case 0x0098 or 0x0144 or 0x0179:
                return MeasureWStrMessage(data, prefixBytes: 2, suffixBytes: 1, out length);
            // 2 + u8 + WStr (Atlas tree rename, writer 0x141fa5e60).
            case 0x02a7:
                return MeasureWStrMessage(data, prefixBytes: 3, suffixBytes: 0, out length);
            // 2 + WStr + WStr (writer 0x141f9b950; 0x0280 sent on area entry by 0x140d59480).
            case 0x0081 or 0x0280:
                return MeasureTwoWStrMessage(data, suffixBytes: 0, out length);
            // 2 + WStr + WStr + u32 + u8 (shop buy, writer 0x141fa1160).
            case 0x012c:
                return MeasureTwoWStrMessage(data, suffixBytes: 5, out length);
            // u16 id + 4 narrow UTF-8 strings (each u16 BE len + bytes; shop delivery quote/order, writer 0x141fa1600).
            case 0x0130 or 0x0138:
                return MeasureFourNarrowStrMessage(data, out length);
            // u16 id, u8 flags, optional u32 when bit 0 is clear (writer 0x141fa1b30, main frame loop 0x140d551f0).
            case 0x0158:
                return Measure0158(data, out length);
            // u16 id, u16 BE len, len bytes (0x02cd writer 0x141fa6480; 0x02fc writer 0x141fa62c0).
            case InstanceTransfer.LeaveAreaAckId or 0x02fc:
                return MeasureBlob16(data, out length);
            // u16 id, u16 BE len1, len1 bytes, u16 BE len2, len2 bytes (writer 0x141fa4140).
            case 0x0281:
                return MeasureTwoBlob16(data, out length);
            // u16 id, u32 BE, u16 BE n, n * u32 BE (salvage box open, writer 0x141fa3bf0 → 0x141faa4a0).
            case 0x026e:
                return MeasureSalvageBox(data, out length);
            // u16 id, u8 count, count * u8 (Heist sign contract, writer 0x141fa3d30 → 0x141fab790).
            case 0x0279:
                return MeasureU8ListMessage(data, prefixBytes: 2, out length);
            // u16 id, u8 wing, u8 count, count * u8 (Heist blueprint crew, writer 0x141fa3fc0 → 0x141fab790).
            case 0x027f:
                return MeasureU8ListMessage(data, prefixBytes: 3, out length);
            // u16 id, u8, u8 n1, n1 bytes, u8 n2, n2 bytes, u8, u8 (recombine, writer 0x141fa49e0).
            case 0x028f:
                return MeasureRecombine(data, out length);
            // u16 id, u32 BE netId, u8 count, count * (u8 + u32 BE + u32 BE) (Necropolis Lantern enter, writer 0x141fa5c10).
            case 0x02a5:
                return MeasureNecropolisLantern(data, out length);
            // u16 id, u8 ruleset, CompactUInt(len), len bytes (ruleset message, writer 0x141fa7c10 / reader 0x141fa7ca0).
            case InstanceRulesets.ActionId:
                return InstanceRulesets.MeasureActionLength(data, out length);
            // Custom mod envelope (non-retail): u16 BE 0xF7E0, u32 BE payloadLen (<= ModEnvelopeMaxPayload), payload bytes.
            case ModEnvelopeId:
                return MeasureModEnvelope(data, out length);
            default:
                if (KnownClientLength(id) is int fixedLen) { length = fixedLen; return WireMeasure.Ok; }
                length = 0;
                return WireMeasure.Invalid;
        }
    }

    static WireMeasure FromNullableLength(int? len, out int length)
    {
        if (len is int v) { length = v; return WireMeasure.Ok; }
        length = 0;
        return WireMeasure.Incomplete;
    }

    static WireMeasure MeasureWStrMessage(ReadOnlySpan<byte> data, int prefixBytes, int suffixBytes, out int length)
    {
        length = 0;
        if (data.Length < prefixBytes) return WireMeasure.Incomplete;
        var status = LoginReader.TryMeasureWStr(data[prefixBytes..], out int textLength);
        if (status != WireMeasure.Ok) return status;
        int total = prefixBytes + textLength + suffixBytes;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureTwoWStrMessage(ReadOnlySpan<byte> data, int suffixBytes, out int length)
    {
        length = 0;
        if (data.Length < 2) return WireMeasure.Incomplete;
        var s1 = LoginReader.TryMeasureWStr(data[2..], out int len1);
        if (s1 != WireMeasure.Ok) return s1;
        int secondAt = 2 + len1;
        var s2 = LoginReader.TryMeasureWStr(data[secondAt..], out int len2);
        if (s2 != WireMeasure.Ok) return s2;
        int total = secondAt + len2 + suffixBytes;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure Measure0158(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 3) return WireMeasure.Incomplete;
        int total = (data[2] & 1) != 0 ? 3 : 7;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureBlob16(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 4) return WireMeasure.Incomplete;
        int total = 4 + BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureTwoBlob16(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 4) return WireMeasure.Incomplete;
        int secondAt = 4 + BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (data.Length - secondAt < 2) return WireMeasure.Incomplete;
        int total = secondAt + 2 + BinaryPrimitives.ReadUInt16BigEndian(data[secondAt..]);
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureSalvageBox(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 8) return WireMeasure.Incomplete;
        int total = 8 + BinaryPrimitives.ReadUInt16BigEndian(data[6..]) * 4;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureRecombine(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 4) return WireMeasure.Incomplete;
        int n2At = 4 + data[3];
        if (n2At >= data.Length) return WireMeasure.Incomplete;
        int total = n2At + 1 + data[n2At] + 2;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureFourNarrowStrMessage(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        int pos = 2;
        for (int i = 0; i < 4; i++)
        {
            if (data.Length - pos < 2) return WireMeasure.Incomplete;
            int strLen = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2 + strLen;
            if (pos > data.Length) return WireMeasure.Incomplete;
        }
        length = pos;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureU8ListMessage(ReadOnlySpan<byte> data, int prefixBytes, out int length)
    {
        length = 0;
        if (data.Length <= prefixBytes) return WireMeasure.Incomplete;
        int total = prefixBytes + 1 + data[prefixBytes];
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureNecropolisLantern(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 7) return WireMeasure.Incomplete;
        int total = 7 + data[6] * 9;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    static WireMeasure MeasureModEnvelope(ReadOnlySpan<byte> data, out int length)
    {
        length = 0;
        if (data.Length < 6) return WireMeasure.Incomplete;
        uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(data[2..]);
        if (payloadLen > ModEnvelopeMaxPayload) return WireMeasure.Invalid;
        int total = 6 + (int)payloadLen;
        if (data.Length < total) return WireMeasure.Incomplete;
        length = total;
        return WireMeasure.Ok;
    }

    /// <summary>First 8 bytes of the 16-byte SipHash-2-4 key for C→S 0x0E (<c>0x142fc9c38</c> in 3.29.3c, loaded by
    /// <c>0x14214ee50</c> and concatenated with login 0x14 / instance 0x27 <c>Data8</c>).</summary>
    public static ReadOnlySpan<byte> OpeningDigestKeyPrefix => [0x8d, 0x75, 0x09, 0xa3, 0xfe, 0xa5, 0x17, 0x81];

    /// <summary>Computes the 12-byte C→S 0x0E <see cref="InstanceOpening.Digest"/>: 8-byte little-endian SipHash-2-4
    /// over <paramref name="unixTime"/> (LE) with key <see cref="OpeningDigestKeyPrefix"/> ‖ <paramref name="data8"/>,
    /// followed by <paramref name="unixTime"/> in big-endian (<c>0x14214ee50</c> / <c>0x141f97550</c>).</summary>
    public static byte[] ComputeOpeningDigest(ReadOnlySpan<byte> data8, uint unixTime)
    {
        if (data8.Length != 8) throw new ArgumentException("Opening Data8 must be 8 bytes.", nameof(data8));
        Span<byte> key = stackalloc byte[16];
        OpeningDigestKeyPrefix.CopyTo(key);
        data8.CopyTo(key[8..]);
        Span<byte> timeLe = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(timeLe, unixTime);
        var digest = new byte[12];
        SipHash24.ComputeBytes(key, timeLe, digest);
        BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(8, 4), unixTime);
        return digest;
    }

    /// <summary>Verifies a client's C→S 0x0E <see cref="InstanceOpening.Digest"/> against <paramref name="data8"/> in
    /// constant time. When <paramref name="nowUnixSeconds"/> is given, also checks that the embedded 32-bit Unix
    /// timestamp is within <paramref name="maxSkewSeconds"/> (handling uint32 wrap safely).</summary>
    public static bool VerifyOpeningDigest(InstanceOpening opening, ReadOnlySpan<byte> data8, long? nowUnixSeconds = null,
        long maxSkewSeconds = 300)
    {
        if (opening is null || opening.Digest.Length != 12 || data8.Length != 8) return false;
        uint clientTime = BinaryPrimitives.ReadUInt32BigEndian(opening.Digest.AsSpan(8, 4));
        Span<byte> key = stackalloc byte[16];
        OpeningDigestKeyPrefix.CopyTo(key);
        data8.CopyTo(key[8..]);
        Span<byte> timeLe = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(timeLe, clientTime);
        Span<byte> expected = stackalloc byte[8];
        SipHash24.ComputeBytes(key, timeLe, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, opening.Digest.AsSpan(0, 8))) return false;
        if (nowUnixSeconds is long now)
        {
            if (maxSkewSeconds < 0) return false;
            long diff = unchecked((int)(clientTime - (uint)now));
            if (Math.Abs(diff) > maxSkewSeconds) return false;
        }
        return true;
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
/// B28 is the league category (world +0xd0, <see cref="GameServer.Core.World.LeagueCategory"/>), 60 for none.
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
/// C→S 0x0F + 0x0E (<c>0x14214ee50</c>, writer <c>0x141f97550</c>). Ticket is login 0x14's u32 at +0x38. Digest is
/// 12 bytes: 8-byte little-endian SipHash-2-4 (<c>0x141fe7d90</c>) over the low 32 bits of <c>_time64(NULL)</c> (LE)
/// keyed by the 8-byte constant at <c>0x142fc9c38</c> (3.29.3c, <see cref="InstanceProtocol.OpeningDigestKeyPrefix"/>)
/// concatenated with 0x14/0x27 <c>Data8</c>, followed by that 32-bit Unix timestamp in big-endian. Address is 0x14's
/// u32 at +0x34. U32_128 is always 0, Realm is 0x40 (international) and Flag is global 0x1445998ce.
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

    /// <summary>Derives the 8-byte ticket-bound <c>Data8</c> nonce sent in login S→C 0x14 and instance S→C 0x27,
    /// distinct from <see cref="Key"/> via the <c>"D8"</c> domain separator (<c>[ticket BE, 0x44, 0x38]</c>).</summary>
    public byte[] Data8(uint ticket)
    {
        Span<byte> input = stackalloc byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(input, ticket);
        input[4] = 0x44;
        input[5] = 0x38;
        Span<byte> mac = stackalloc byte[64];
        HMACSHA512.HashData(secret, input, mac);
        return mac[..8].ToArray();
    }

    /// <summary>The server's Salsa20 pair for a ticket (mirror of the client's isClient=1 setup).</summary>
    public ConnectionCiphers Ciphers(uint ticket) => new(Key(ticket), isClient: false);
}
