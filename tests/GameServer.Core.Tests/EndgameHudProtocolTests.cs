using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Tests;

public sealed class EndgameHudProtocolTests
{
    static readonly byte[] SampleTick = [0x00, 0x1b, 0x01, 0x00, 0x00, 0x00, 0x02];

    static void AssertFramed(byte[] packet, ushort expectedId)
    {
        byte[] coalesced = [.. packet, .. SampleTick];
        var parsed = InstanceProtocol.SplitClientMessages(coalesced, out byte[] partial, out byte[] unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(expectedId, parsed[0].Id);
        Assert.Equal(packet, parsed[0].Bytes);
        Assert.Equal(InstanceProtocol.TickId, parsed[1].Id);
    }

    [Fact]
    public void Atlas_and_hinekora_wire_layouts_match_client_contract()
    {
        // S->C 0x01a5: Atlas voidstone T16 delta (u16 BE 0x01a5, u8 regionIndex, u8 upgraded).
        Assert.Equal([0x01, 0xa5, 0x02, 0x01], InstanceMapDevice.AtlasVoidstoneDelta(2, true));
        Assert.Equal([0x01, 0xa5, 0x03, 0x00], InstanceMapDevice.AtlasVoidstoneDelta(3, false));

        // S->C 0x7b kind 0x13: Atlas Scrying UI open.
        byte[] openScry = InstanceWaypoints.OpenUi(InstanceWaypoints.AtlasScryingUi, 1, 0x1234u);
        Assert.Equal((ushort)0x007b, BinaryPrimitives.ReadUInt16BigEndian(openScry));
        Assert.Equal((byte)0x13, openScry[2]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(openScry.AsSpan(3, 4)));
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32BigEndian(openScry.AsSpan(7, 4)));

        // C->S 0x01a7: Atlas Scrying swap (10B: u16 BE id, u16 BE from, u16 BE to, u32 BE objectId).
        byte[] scryReq = new LoginWriter().U16(InstanceMapDevice.AtlasScryId).U16(0x1122).U16(0x3344).U32(0x55667788u).ToArray();
        Assert.Equal(InstanceMapDevice.AtlasScryLength, scryReq.Length);
        AssertFramed(scryReq, InstanceMapDevice.AtlasScryId);
        var scry = InstanceMapDevice.ReadAtlasScry(scryReq);
        Assert.Equal((ushort)0x1122, scry.FromAreaHash);
        Assert.Equal((ushort)0x3344, scry.ToAreaHash);
        Assert.Equal(0x55667788u, scry.ObjectId);

        // C->S 0x0075 / 0x0076: Use item / item type on Atlas node.
        byte[] useNode = new LoginWriter().U16(InstanceMapDevice.UseItemOnAtlasNodeId).U32(1u).U32(42u).U16(0x9a23).ToArray();
        Assert.Equal(InstanceMapDevice.UseItemOnAtlasNodeLength, useNode.Length);
        AssertFramed(useNode, InstanceMapDevice.UseItemOnAtlasNodeId);
        var u75 = InstanceMapDevice.ReadUseItemOnAtlasNode(useNode);
        Assert.Equal((1u, 42u, (ushort)0x9a23), (u75.InventoryId, u75.ItemId, u75.NodeHash16));

        byte[] useTypeNode = new LoginWriter().U16(InstanceMapDevice.UseItemTypeOnAtlasNodeId)
            .U32(0xaabbccddu).U32(1u).U16(2).U16(3).U8(4).U16(0x9a23).ToArray();
        Assert.Equal(InstanceMapDevice.UseItemTypeOnAtlasNodeLength, useTypeNode.Length);
        AssertFramed(useTypeNode, InstanceMapDevice.UseItemTypeOnAtlasNodeId);
        var u76 = InstanceMapDevice.ReadUseItemTypeOnAtlasNode(useTypeNode);
        Assert.Equal((0xaabbccddu, 1u, (ushort)2, (ushort)3, (byte)4, (ushort)0x9a23),
            (u76.TypeHash, u76.InventoryId, u76.TabIndex, u76.SubIndex, u76.Flags, u76.NodeHash16));

        // S->C 0x012a: Atlas Vault Pass.
        byte[] pass = InstanceMapDevice.AtlasVaultPass(45, "VaultPass329", purchased: true, active: true, trackFlags: 3,
            unlockedMask: 0x0123456789abcdefUL, claimedMask: 0x00000000000000ffUL, hasUnclaimedRewards: true);
        var pr = new LoginReader(pass);
        Assert.Equal(InstanceMapDevice.AtlasVaultPassId, pr.U16());
        Assert.Equal((ushort)45, pr.U16());
        Assert.Equal("VaultPass329", pr.WStr());
        Assert.Equal((byte)1, pr.U8());
        Assert.Equal((byte)1, pr.U8());
        Assert.Equal((byte)3, pr.U8());
        Assert.Equal(0x0123456789abcdefUL, BinaryPrimitives.ReadUInt64LittleEndian(pr.Fixed(8)));
        Assert.Equal(0x00000000000000ffUL, BinaryPrimitives.ReadUInt64LittleEndian(pr.Fixed(8)));
        Assert.Equal((byte)1, pr.U8());
        Assert.Equal(pass.Length, pr.Position);

        // S->C 0x2c9 ruleset 99 sub-id 0: Primordial Altar HUD (u16 BE 0x02c9, u8 row 99, compact-uint len, payload).
        byte[] altar = InstanceRulesets.PrimordialAltarHud(
            [new(0x1234, [25, -10])], [], [new(0x5678, [100])], [], [], []);
        Assert.Equal((ushort)0x02c9, BinaryPrimitives.ReadUInt16BigEndian(altar));
        Assert.Equal(InstanceRulesets.PrimordialInfluenceRulesetRow, altar[2]);
        Assert.Equal((byte)(altar.Length - 4), altar[3]); // compact-uint payload length (< 128)
        Assert.Equal((byte)0, altar[4]); // sub-id 0
        Assert.Equal((byte)1, altar[5]); // playerUpsides count
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(altar.AsSpan(6, 2)));

        // C->S 0x029e (28B) & 0x029f (25B): Hinekora's Lock dual-item and type foresight.
        byte[] dual = new LoginWriter().U16(InstanceItems.DualCurrencyApplicationForesightRequestId)
            .U16(7).U32(1u).U32(10u).U32(1u).U32(20u).U32(1u).U32(30u).ToArray();
        Assert.Equal(InstanceItems.DualCurrencyApplicationForesightRequestLength, dual.Length);
        AssertFramed(dual, InstanceItems.DualCurrencyApplicationForesightRequestId);
        var d = InstanceItems.ReadDualCurrencyApplicationForesightRequest(dual);
        Assert.Equal(((ushort)7, 1u, 10u, 1u, 20u, 1u, 30u),
            (d.Key, d.CurrencyInventory, d.CurrencyId, d.FirstTargetInventory, d.FirstTargetId, d.SecondTargetInventory, d.SecondTargetId));

        byte[] typed = new LoginWriter().U16(InstanceItems.CurrencyTypeApplicationForesightRequestId)
            .U16(3).U32(0x11223344u).U32(1u).U16(2).U16(4).U32(1u).U32(20u).U8(5).ToArray();
        Assert.Equal(InstanceItems.CurrencyTypeApplicationForesightRequestLength, typed.Length);
        AssertFramed(typed, InstanceItems.CurrencyTypeApplicationForesightRequestId);
        var t = InstanceItems.ReadCurrencyTypeApplicationForesightRequest(typed);
        Assert.Equal(((ushort)3, 0x11223344u, 1u, (ushort)2, (ushort)4, 1u, 20u, (byte)5),
            (t.Key, t.CurrencyTypeHash, t.CurrencyInventory, t.TabIndex, t.SubIndex, t.TargetInventory, t.TargetId, t.Flags));
    }

    [Fact]
    public void Delve_deepwater_labyrinth_and_currency_exchange_wire_layouts_match_client_contract()
    {
        // Delve S->C 0x0227 (Azurite), 0x022c (ChartDelta), 0x0242 (RouteConfirmed).
        Assert.Equal([0x02, 0x27, 0x00, 0x00, 0x01, 0xf4], InstanceDelve.AzuriteChanged(500));

        byte[] delta = InstanceDelve.ChartDelta([new(1, 2, 3, 4), new(-1, 5, 0, 7)]);
        Assert.Equal(4 + 2 * 16, delta.Length);
        Assert.Equal(InstanceDelve.ChartDeltaId, BinaryPrimitives.ReadUInt16BigEndian(delta));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(delta.AsSpan(2, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(delta.AsSpan(4, 4)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32BigEndian(delta.AsSpan(20, 4)));

        byte[] route = InstanceDelve.RouteConfirmed([new(0, 1, 2, 0), new(0, 1, 2, 1)]);
        Assert.Equal(3 + 2 * 16, route.Length);
        Assert.Equal(InstanceDelve.RouteConfirmedId, BinaryPrimitives.ReadUInt16BigEndian(route));
        Assert.Equal((byte)2, route[2]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(route.AsSpan(31, 4)));

        // Deepwater S->C 0x7b kind 0x72 (CurrentVoyage).
        byte[] voyage = InstanceWaypoints.OpenUi(InstanceWaypoints.DeepwaterActiveVoyageUi);
        Assert.Equal((byte)0x72, voyage[2]);

        // Labyrinth Divine Font S->C 0x0246, 0x02a2 and C->S 0x0245, 0x0247, 0x02a3.
        byte[] opts = InstanceLabyrinth.CraftOptions([0x1122, 0x3344], remainingUses: 2);
        Assert.Equal([0x02, 0x46, 0x02, 0x11, 0x22, 0x33, 0x44, 0x02], opts);

        var itemA = new Item(0x11223344u, [], 0);
        var itemB = new Item(0x55667788u, [], 0);
        byte[] results = InstanceLabyrinth.CraftResultOptionsList([itemA, itemB]);
        Assert.Equal(InstanceLabyrinth.CraftResultOptionsListId, BinaryPrimitives.ReadUInt16BigEndian(results));
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(results.AsSpan(2, 8)));
        Assert.Equal([.. itemA.Wire(), .. itemB.Wire()], results[10..]);

        byte[] c245 = [0x02, 0x45, 0x01];
        byte[] c247 = [0x02, 0x47, 0x11, 0x22];
        byte[] c2a3 = [0x02, 0xa3, 0x02];
        AssertFramed(c245, InstanceLabyrinth.CraftApplyId);
        AssertFramed(c247, InstanceLabyrinth.CraftSelectId);
        AssertFramed(c2a3, InstanceLabyrinth.CraftResultSelectId);
        Assert.Equal((byte)1, InstanceLabyrinth.ReadCraftApply(c245));
        Assert.Equal((ushort)0x1122, InstanceLabyrinth.ReadCraftSelect(c247));
        Assert.Equal((byte)2, InstanceLabyrinth.ReadCraftResultSelect(c2a3));

        // Currency Exchange S->C 0x02ef (QueryOrderResult, 16B).
        byte[] qres = InstanceCurrencyExchange.QueryOrderResult(queryId: 99u, status: 0, bought: 12u, sold: 34u);
        Assert.Equal(16, qres.Length);
        Assert.Equal(InstanceCurrencyExchange.QueryOrderResultId, BinaryPrimitives.ReadUInt16BigEndian(qres));
        Assert.Equal(99u, BinaryPrimitives.ReadUInt32BigEndian(qres.AsSpan(2, 4)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(qres.AsSpan(6, 2)));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32BigEndian(qres.AsSpan(8, 4)));
        Assert.Equal(34u, BinaryPrimitives.ReadUInt32BigEndian(qres.AsSpan(12, 4)));
    }

    [Fact]
    public void Chat_by_id_awards_notices_and_non_quest_tracker_wire_layouts_match_client_contract()
    {
        // S->C 0x18: DeathMessage compatibility + full BackendError / ClientString / ClientString2 with raw LE flags & chatColour.
        byte[] death = InstanceProtocol.DeathMessage("Exile");
        Assert.Equal((ushort)0x0018, BinaryPrimitives.ReadUInt16BigEndian(death));
        Assert.Equal(InstanceProtocol.BackendErrorId.PlayerSlain, BinaryPrimitives.ReadUInt16BigEndian(death.AsSpan(2, 2)));
        Assert.Equal((ushort)0xffff, BinaryPrimitives.ReadUInt16BigEndian(death.AsSpan(4, 2)));

        byte[] csMsg = InstanceProtocol.GameMessage(
            InstanceProtocol.GameMessageSource.ClientString, id: 0x0042, npcRow: 0x0102,
            flags: 0x0304, holdTimeMs: 5000u, chatColour: 0x00000007u,
            numericArgs: [(0, 12)], stringArgs: ["Oak"]);
        Assert.Equal((ushort)0x8042, BinaryPrimitives.ReadUInt16BigEndian(csMsg.AsSpan(2, 2)));
        Assert.Equal((ushort)0x0102, BinaryPrimitives.ReadUInt16BigEndian(csMsg.AsSpan(4, 2)));
        Assert.Equal((ushort)0x0304, BinaryPrimitives.ReadUInt16LittleEndian(csMsg.AsSpan(6, 2)));
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32BigEndian(csMsg.AsSpan(8, 4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(csMsg.AsSpan(12, 4)));
        Assert.Equal((byte)1, csMsg[16]); // numArgCount
        Assert.Equal((byte)1, csMsg[17]); // strArgCount

        byte[] cs2Msg = InstanceProtocol.GameMessage(InstanceProtocol.GameMessageSource.ClientString2, id: 5);
        Assert.Equal((ushort)(InstanceProtocol.BackendErrorMaxId + 1 + 5), BinaryPrimitives.ReadUInt16BigEndian(cs2Msg.AsSpan(2, 2)));

        // S->C 0x01d5 (AwardDisplay), 0x01d6 (CraftAward), 0x02cc (ClientStringNotice raw LE), 0x0019 (ServerBroadcast).
        Assert.Equal([0x01, 0xd5, 0x06], InstanceProtocol.AwardDisplay(InstanceProtocol.AwardDisplayRow.FavouredMapSlotUnlocked));
        Assert.Equal([0x01, 0xd6, 0xde, 0xad, 0xbe, 0xef], InstanceProtocol.CraftAward(0xdeadbeefu));
        Assert.Equal([0x02, 0xcc, 0x34, 0x12], InstanceProtocol.ClientStringNotice(0x1234));

        byte[] broadcast = InstanceChat.ServerBroadcast("Server restarting in 5 minutes.");
        var br = new LoginReader(broadcast);
        Assert.Equal(InstanceChat.ServerBroadcastId, br.U16());
        Assert.Equal("Server restarting in 5 minutes.", br.WStr());
        Assert.Equal(broadcast.Length, br.Position);

        // S->C 0x015f (NonQuestTrackerRecord).
        byte[] tracker = InstanceTimers.NonQuestTrackerRecord(trackerId: 12, flags: 1, [
            new(Key: 3, ClientStringRow1Based: 50, Value: 4, NumericArgs: [(0, 4)], StringArgs: ["Boss"]),
        ]);
        var tr = new LoginReader(tracker);
        Assert.Equal(InstanceTimers.NonQuestTrackerRecordId, tr.U16());
        Assert.Equal((ushort)12, tr.U16());
        Assert.Equal((byte)1, tr.U8());
        Assert.Equal((byte)1, tr.U8());
        Assert.Equal((ushort)3, tr.U16());
        Assert.Equal((ushort)50, tr.U16());
        Assert.Equal((ushort)4, tr.U16());
        Assert.Equal((byte)1, tr.U8());
        Assert.Equal((byte)1, tr.U8());
        Assert.Equal((byte)0, tr.U8());
        Assert.Equal((byte)4, tr.U8()); // CompactInt(4)
        Assert.Equal("Boss", tr.WStr());
        Assert.Equal(tracker.Length, tr.Position);
    }
}
