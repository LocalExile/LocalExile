using System.Runtime.CompilerServices;

namespace GameServer.Core.Protocol.Instance;

/// <summary>Which network mode sends a C→S message: <see cref="Shared"/> for both, or only one of them.</summary>
public enum PacketMode { Shared, Predictive, Lockstep }

/// <summary>How long a variable C→S message is, read from its start (id included); null until enough bytes arrived.</summary>
public delegate int? MessageLength(ReadOnlySpan<byte> message);

/// <summary>One C→S instance message: its id, its constant's name, the mode that sends it, and its length: fixed, or
/// read from the message by <see cref="Measure"/>.</summary>
public sealed record ClientPacket(ushort Id, string Name, PacketMode Mode, int? FixedLength, MessageLength? Measure)
{
    /// <summary>The message's whole length at the start of <paramref name="at"/>, or null until enough bytes arrived.</summary>
    public int? Length(ReadOnlySpan<byte> at) => FixedLength ?? Measure!(at);
}

/// <summary>
/// Every encrypted C→S instance message the Gateway frames, in one place (the plaintext opening 0x0f and 0x0e is read
/// by <see cref="InstanceProtocol.TryReadOpening"/>). There is no length prefix: an id missing here cannot be framed, and
/// the rest of the read is lost. The mode column comes from the client's senders (07 §6, "Network mode"): the input
/// senders 0x1421660b0, 0x1421664c0, 0x142166660, 0x142166800 and 0x14216ccb0 pick their messages by the world's mode,
/// nothing else sends those ids, and every other message is sent the same way in both modes. A client never sends
/// both kinds on one connection.
/// </summary>
public static class ClientPackets
{
    const PacketMode Predictive = PacketMode.Predictive, Lockstep = PacketMode.Lockstep;

    /// <summary>A message of fixed length.</summary>
    static ClientPacket P(ushort id, int length, PacketMode mode = PacketMode.Shared,
        [CallerArgumentExpression(nameof(id))] string name = "") => new(id, name, mode, length, null);

    /// <summary>A message whose length is read from it.</summary>
    static ClientPacket V(ushort id, MessageLength measure, PacketMode mode = PacketMode.Shared,
        [CallerArgumentExpression(nameof(id))] string name = "") => new(id, name, mode, null, measure);

    static readonly ClientPacket[] Rows =
    [
        // Messages whose length is read from them.
        V(InstanceDelve.RouteId, InstanceDelve.RouteLength),
        V(InstanceChat.ChatId, InstanceChat.Length),
        V(InstanceItems.CommandId, InstanceItems.CommandLength),
        V(InstanceNpcs.BuyId, InstanceNpcs.BuyLength),
        V(InstanceStash.ReorderTabsId, InstanceStash.ReorderTabsLength),
        V(InstanceStash.RenameTabId, InstanceStash.RenameTabLength),
        P(InstanceStash.ColourTabId, InstanceStash.ColourTabLength),
        V(InstanceStash.MoveToAffinityTabId, InstanceStash.MoveToAffinityTabLength),
        V(InstanceStash.SetAffinitiesId, InstanceStash.SetAffinitiesLength),
        V(InstanceNpcs.CtrlBuyId, InstanceNpcs.CtrlBuyLength),
        V(InstanceWaypoints.VisitHideoutId, InstanceWaypoints.VisitHideoutLength),
        V(InstanceWaypoints.VisitMenagerieId, InstanceWaypoints.VisitHideoutLength),
        V(InstanceSocial.PartyInviteId, InstanceSocial.PartyInviteLength),
        V(InstanceSocial.PartyKickId, InstanceSocial.PartyInviteLength),
        V(InstanceSocial.PartyTravelId, InstanceSocial.PartyInviteLength),
        V(InstanceTransfer.LeaveAreaAckId, InstanceTransfer.LeaveAreaAckLength),
        V(InstanceSocial.FriendAddId, InstanceSocial.FriendAddLength),
        // Messages of fixed length.
        P(InstanceDivination.TurnInId, InstanceDivination.TurnInLength),
        P(InstanceDivination.MoveId, InstanceDivination.MoveLength),
        P(InstanceDelve.UpgradesRequestId, 2),
        P(InstanceDelve.ChartRequestId, 2),
        P(InstanceDelve.UpgradeId, 4),
        P(InstanceDelve.PurchaseId, 4),
        P(InstanceMonsterStatue.CreateId, InstanceMonsterStatue.CreateLength),
        P(InstanceItems.CurrencyApplicationForesightRequestId, InstanceItems.CurrencyApplicationForesightRequestLength),
        P(InstanceProtocol.TickId, 7),
        P(InstanceProtocol.AreaGeneratedId, 10),
        P(InstanceProtocol.UseSkillAtLocationId, 16, Predictive),
        // 0x015a, 0x01ef and 0x0129 are id only; 0x0099 is two u8 (class 0x1435cac90, write 0x141f9bc30, meaning unknown),
        // sent on entering an area with the chat channel joins (live captures 2026-10-16).
        P(0x015a, 2),
        P(0x01ef, 2),
        P(0x0099, 4),
        P(0x0129, 2),
        P(InstanceChat.LinkItemId, InstanceChat.LinkItemLength),
        P(InstanceChat.LinkOtherId, InstanceChat.LinkOtherLength),
        P(InstanceChat.LinkAnotherId, InstanceChat.LinkAnotherLength),
        P(InstanceProtocol.TutorialCompleteId, InstanceProtocol.TutorialCompleteLength),
        // Primary 3.29.3c writer: 0x141f99e90 (u16 + 4/2/2/4/1/1 bytes).
        P(0x0059, 16),
        P(InstanceSocial.TradeRequestId, InstanceSocial.TradeRequestLength),
        P(InstanceSocial.TradeRequestAcceptId, InstanceSocial.TradeRequestAcceptLength),
        P(InstanceSocial.PartyAcceptId, InstanceSocial.PartyAcceptLength),
        P(InstanceSocial.PartyLeaveId, InstanceSocial.PartyLeaveLength),
        P(InstanceChat.JoinChannelId, InstanceChat.JoinChannelLength),
        P(InstancePantheon.ChooseGodId, InstancePantheon.ChooseGodLength),
        P(InstanceWaypoints.VisitDelveHubId, InstanceWaypoints.VisitDelveHubLength),
        P(InstanceWaypoints.VisitSanctumId, InstanceWaypoints.VisitSanctumLength),
        P(InstanceWaypoints.VisitKingsmarchId, InstanceWaypoints.VisitKingsmarchLength),
        P(InstanceWaypoints.VisitHeistHubId, InstanceWaypoints.VisitHeistHubLength),
        P(InstanceWaypoints.VisitChayulaHubId, InstanceWaypoints.VisitChayulaHubLength),
        P(InstanceWaypoints.VisitDeepwaterHubId, InstanceWaypoints.VisitDeepwaterHubLength),
        P(InstanceTransfer.UseSkillOnObjectId, 12, Predictive),
        P(InstanceTransfer.ChooseInstanceId, 3),
        P(InstanceProtocol.ResurrectId, 3),
        P(InstanceItems.DropItemId, 2),
        P(InstanceItems.PickUpItemId, 13),
        P(InstanceItems.PlaceItemId, 18),
        // Ctrl + click with the map device open: a map sends one of these, never 0xe8 (that is the Atlas Idol path).
        P(InstanceMapDevice.MoveIntoDeviceId, InstanceMapDevice.MoveItemLength),
        P(InstanceMapDevice.MoveOutOfDeviceId, InstanceMapDevice.MoveItemLength),
        P(InstanceItems.MissionSlotMoveId, InstanceItems.LeagueWindowMoveLength),
        P(InstanceItems.CraftingSlotMoveId, InstanceItems.LeagueWindowMoveLength),
        P(InstanceItems.LockerMoveId, InstanceItems.LeagueWindowMoveLength),
        P(InstanceItems.ExtractionMoveId, InstanceItems.LeagueWindowMoveLength),
        P(InstanceItems.SocketStorageMoveId, InstanceItems.LeagueWindowMoveLength),
        P(InstanceItems.CollectionTakeId, InstanceItems.CollectionTakeLength),
        P(InstanceItems.InventoryRequestId, InstanceItems.InventoryRequestLength),
        P(InstanceItems.UseItemId, InstanceItems.UseItemLength),
        P(InstanceItems.UseItemOnId, InstanceItems.UseItemOnLength),
        P(InstanceItems.UseItemOnObjectId, InstanceItems.UseItemOnObjectLength),
        P(InstanceItems.UseItemOnPassiveId, InstanceItems.UseItemOnPassiveLength),
        P(InstanceItems.UseFlaskId, InstanceItems.UseFlaskLength),
        P(InstanceItems.UseItemTypeOnId, InstanceItems.UseItemTypeOnLength),
        P(InstanceItems.ItemActionId, InstanceItems.ItemActionLength),
        P(InstanceItems.SplitStackId, InstanceItems.SplitStackLength),
        P(InstanceItems.SocketGemId, 16),
        P(InstanceItems.UnsocketGemId, 17),
        P(InstanceProtocol.BindSkillId, 5),
        P(InstanceProtocol.AttackInPlaceId, 4),
        P(InstanceProtocol.SlotMask52Id, 4),
        P(InstanceProtocol.ExitToCharacterSelectId, 2),
        P(InstanceProtocol.ExitToLoginId, 3),
        P(InstanceItems.LevelUpGemId, 14),
        P(InstanceItems.LevelUpAllGemsId, InstanceItems.LevelUpAllGemsLength),
        P(InstanceItems.LockGemId, InstanceItems.LockGemLength),
        P(InstanceEntities.AllocatePassiveId, 7),
        P(InstanceEntities.MasteryEffectId, InstanceEntities.MasteryEffectLength),
        P(InstanceEntities.RefundPassiveId, InstanceEntities.RefundPassiveLength),
        P(InstanceEntities.CancelBuffId, InstanceEntities.CancelBuffLength),
        P(InstanceNpcs.VoteAnswerId, InstanceNpcs.VoteAnswerLength),
        P(InstanceEntities.RefundAllPassivesId, 2),
        P(InstanceEntities.PanelToggleId, 3),
        P(InstanceEntities.AtlasTreeTabId, InstanceEntities.AtlasTreeTabLength),
        P(InstanceEntities.AtlasRefundAllId, InstanceEntities.AtlasTreeTabLength),
        P(InstanceEntities.AtlasTreeWidgetId, InstanceEntities.AtlasTreeTabLength),
        P(InstanceWaypoints.TravelId, InstanceWaypoints.TravelLength),
        P(InstanceNpcs.DialogueClosedId, 2),
        P(InstanceNpcs.OptionId, InstanceNpcs.OptionLength),
        P(InstanceNpcs.TakeAllRewardsId, InstanceNpcs.TakeAllRewardsLength),
        P(InstanceNpcs.TradeAnswerId, InstanceNpcs.TradeAnswerLength),
        P(InstanceNpcs.TradeOfferId, InstanceNpcs.TradeOfferLength),
        P(InstanceNpcs.TradeTakeBackId, InstanceNpcs.TradeTakeBackLength),
        P(InstanceNpcs.TradeMoveAllId, InstanceNpcs.TradeMoveAllLength),
        P(InstanceNpcs.TradeAcceptId, InstanceNpcs.TradeAcceptLength),
        P(InstanceNpcs.TradeUnacceptId, InstanceNpcs.TradeUnacceptLength),
        P(InstanceStash.RequestTabId, InstanceStash.RequestTabLength),
        P(InstanceStash.MoveTabId, InstanceStash.MoveTabLength),
        P(InstanceStash.DeleteFolderId, InstanceStash.DeleteFolderLength),
        P(InstanceStash.MoveToTabId, InstanceStash.MoveToTabLength),
        P(InstanceStash.TakeFromTabId, InstanceStash.TakeFromTabLength),
        P(InstanceMapDevice.ActivateId, InstanceMapDevice.ActivateLength),
        P(InstanceMapDevice.SelectMapId, InstanceMapDevice.SelectMapLength),
        P(InstanceMapDevice.MoveId, InstanceMapDevice.MoveLength),
        P(InstanceMapDevice.SocketId, InstanceMapDevice.SocketLength),
        P(InstanceEntities.AtlasAllocateId, InstanceEntities.AtlasAllocateLength),
        P(InstanceEntities.AtlasRefundId, InstanceEntities.AtlasRefundLength),
        P(InstanceProtocol.SwapWeaponsId, 3),
        P(InstanceProtocol.RetargetActionId, InstanceProtocol.RetargetActionLength),
        // Aim updates continue an action that already selected the mode. A native predictive Cyclone sends 0x197
        // after its 0x25 start. The standing-channel form (0x198) uses the same continuation path.
        P(InstanceProtocol.ChannelUpdateId, 10),
        P(InstanceProtocol.ChannelUpdateAltId, 10),
        P(InstanceProtocol.ChannelEndId, 2, Predictive),
        P(InstanceProtocol.LockstepMoveStartId, 15, Lockstep),
        P(InstanceProtocol.LockstepMoveUpdateId, 10, Lockstep),
        P(InstanceProtocol.LockstepMoveStopId, 2, Lockstep),
        P(InstanceProtocol.LockstepUseOnObjectId, 11, Lockstep),
        P(InstanceProtocol.LockstepSkillStopId, 4, Lockstep),
        P(InstanceProtocol.UseInstantSkillAtLocationId, 12),
        P(InstanceProtocol.UseInstantSkillId, 8),
        P(InstancePause.RequestId, InstancePause.RequestLength),
        P(InstanceMicrotransactions.SlotId, InstanceMicrotransactions.SlotLength),
        P(InstanceWaypoints.LabyrinthChosenId, InstanceWaypoints.LabyrinthChosenLength),
        P(InstanceWaypoints.AscendancyChosenId, InstanceWaypoints.AscendancyChosenLength),
        P(InstanceWaypoints.DescendancyChosenId, InstanceWaypoints.DescendancyChosenLength),
        P(InstanceMicrotransactions.SkillPickId, InstanceMicrotransactions.SkillPickLength),
        P(InstanceMicrotransactions.RequestId, InstanceMicrotransactions.RequestLength),
        P(InstanceSkinTransfers.BindId, InstanceSkinTransfers.BindLength),
        P(InstanceSkinTransfers.ReclaimId, InstanceSkinTransfers.ReclaimLength),
        P(InstanceHideouts.SelectId, InstanceHideouts.SelectLength),
        P(InstanceHideoutEditing.MoveId, InstanceHideoutEditing.MoveLength),
        P(InstanceBench.CraftId, InstanceBench.CraftLength),
        P(InstanceBench.MoveId, InstanceBench.MoveLength),
        P(InstanceHarvest.CraftId, InstanceHarvest.CraftLength),
        P(InstanceCurrencyExchange.RequestOrdersId, InstanceCurrencyExchange.RequestOrdersLength),
        P(InstanceCurrencyExchange.PlaceOrderId, InstanceCurrencyExchange.PlaceOrderLength),
        P(InstanceCurrencyExchange.CloseOrderId, InstanceCurrencyExchange.CloseOrderLength),
        P(InstanceCurrencyExchange.ClaimId, InstanceCurrencyExchange.ClaimLength),
        P(InstanceCurrencyExchange.RequestMarketId, InstanceCurrencyExchange.RequestMarketLength),
        P(InstanceCurrencyExchange.RequestPartnersId, InstanceCurrencyExchange.RequestPartnersLength),
        P(InstanceCurrencyExchange.RequestFeeId, InstanceCurrencyExchange.RequestFeeLength),
        P(InstancePvp.LeaveQueueId, 2),
        P(InstancePvp.WithdrawId, 2),
        // Remaining exact fixed-length C→S instance opcodes from 3.29.3c (Agents/docs/protocol/c2s.json), excluding
        // plaintext opening 0x000e/0x000f and 0x001a (ExitToLoginId above).
        .. new ushort[] { 0x0038, 0x012e, 0x013c, 0x013e, 0x0159, 0x0168, 0x0169, 0x016a, 0x016c, 0x0171,
            0x0186, 0x018a, 0x019b, 0x020e, 0x021e, 0x0224, 0x0243, 0x024c, 0x0257,
            0x025d, 0x0260, 0x0264, 0x027e, 0x0296, 0x02a6, 0x02ac, 0x02ce, 0x02d5 }.Select(id => P(id, 2, name: $"0x{id:x4}")),
        .. new ushort[] { 0x005e, 0x0083, 0x00ac, 0x00d9, 0x00da, 0x0156, 0x0187, 0x01f7, 0x01f8,
            0x0245, 0x024f, 0x0297, 0x0298, 0x02a3, 0x02a4 }.Select(id => P(id, 3, name: $"0x{id:x4}")),
        .. new ushort[] { 0x011d, 0x0142, 0x0143, 0x016b, 0x01fd, 0x021d, 0x0221, 0x0222, 0x0247, 0x0255, 0x02b1,
            0x02d0, 0x02d1 }.Select(id => P(id, 4, name: $"0x{id:x4}")),
        .. new ushort[] { 0x0249, 0x027d }.Select(id => P(id, 5, name: $"0x{id:x4}")),
        .. new ushort[] { 0x005c, 0x005d, 0x0066, 0x0069, 0x00a2, 0x00d0, 0x01fc, 0x0206, 0x020b,
            0x020f, 0x0217, 0x0218, 0x0259, 0x026b, 0x0275, 0x027a, 0x0284, 0x0288, 0x0289,
            0x02a1, 0x02fd }.Select(id => P(id, 6, name: $"0x{id:x4}")),
        .. new ushort[] { 0x002e, 0x0065, 0x0068, 0x0107, 0x0152, 0x017c, 0x017f, 0x0180, 0x0185, 0x01da,
            0x01dc, 0x01e0, 0x01fb, 0x01fe, 0x0216, 0x028e, 0x02d6 }.Select(id => P(id, 7, name: $"0x{id:x4}")),
        .. new ushort[] { 0x0062, 0x00ef, 0x00f0, 0x00f1, 0x00f2, 0x00f8, 0x00fa, 0x00fb, 0x00fc, 0x00ff, 0x0100,
            0x0102, 0x0104, 0x0105, 0x0108, 0x0109, 0x010d, 0x01ec }.Select(id => P(id, 8, name: $"0x{id:x4}")),
        .. new ushort[] { 0x00e2, 0x00f4, 0x00fd, 0x0106, 0x010a, 0x010b, 0x010c }.Select(id => P(id, 9, name: $"0x{id:x4}")),
        .. new ushort[] { 0x0061, 0x00f9, 0x013a, 0x01a7, 0x01d8, 0x01d9, 0x0265, 0x029b, 0x02d3 }.Select(id => P(id, 10, name: $"0x{id:x4}")),
        .. new ushort[] { 0x0057, 0x0060, 0x0063, 0x0064, 0x0177 }.Select(id => P(id, 11, name: $"0x{id:x4}")),
        .. new ushort[] { 0x005f, 0x0075, 0x00b4, 0x00f5, 0x00f7, 0x00fe, 0x0101, 0x0110, 0x02b0 }.Select(id => P(id, 12, name: $"0x{id:x4}")),
        P(0x002c, 13),
        .. new ushort[] { 0x0067, 0x00cf }.Select(id => P(id, 14, name: $"0x{id:x4}")),
        .. new ushort[] { 0x005a, 0x0114 }.Select(id => P(id, 16, name: $"0x{id:x4}")),
        .. new ushort[] { 0x0076, 0x0078 }.Select(id => P(id, 17, name: $"0x{id:x4}")),
        .. new ushort[] { 0x0079, 0x0283, 0x0294, 0x0299 }.Select(id => P(id, 18, name: $"0x{id:x4}")),
        P(0x0089, 20),
        P(0x002b, 21),
        P(0x029f, 25),
        .. new ushort[] { 0x0073, 0x0116 }.Select(id => P(id, 26, name: $"0x{id:x4}")),
        P(0x029e, 28),
    ];

    static readonly Dictionary<ushort, ClientPacket> ById = Index();

    static Dictionary<ushort, ClientPacket> Index()
    {
        var byId = new Dictionary<ushort, ClientPacket>();
        foreach (var row in Rows)
            if (!byId.TryAdd(row.Id, row)) throw new InvalidOperationException($"C→S 0x{row.Id:x4} is listed twice ({byId[row.Id].Name}, {row.Name})");
        return byId;
    }

    public static IReadOnlyCollection<ClientPacket> All => Rows;

    public static ClientPacket? Find(ushort id) => ById.GetValueOrDefault(id);

    /// <summary>The mode that sends <paramref name="id"/>; Shared for a message both send, and for one not listed.</summary>
    public static PacketMode ModeOf(ushort id) => Find(id)?.Mode ?? PacketMode.Shared;

    /// <summary>The length of the message starting at <paramref name="at"/> (its id first): int.MaxValue when its length
    /// is read from it and not enough bytes arrived yet, null when the id is not listed.</summary>
    public static int? Length(ushort id, ReadOnlySpan<byte> at) =>
        Find(id) is { } packet ? packet.Length(at) ?? int.MaxValue : null;
}
