using System.Buffers.Binary;
using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Xunit;

namespace GameServer.Core.Tests;

public sealed class InstanceVillageTests
{
    [Fact]
    public void Ruleset_and_village_s2c_and_c2s_codecs_match_3293c_wire_layout()
    {
        // S→C 0x001f AttachRuleset(0x9b): u16be 0x001f, u8 0x9b, u32be 0
        byte[] attach = InstanceVillage.AttachRuleset();
        Assert.Equal([0x00, 0x1f, 0x9b, 0x00, 0x00, 0x00, 0x00], attach);

        // S→C 0x001e RulesetEvent: u16be 0x001e, u32be entityId, u16be len, utf8
        byte[] ev = InstanceVillage.RulesetEvent(InstanceVillage.EventCheckRewardIconVisibility, 0x12345678u);
        Assert.Equal(0x001e, BinaryPrimitives.ReadUInt16BigEndian(ev));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32BigEndian(ev.AsSpan(2)));
        ushort evLen = BinaryPrimitives.ReadUInt16BigEndian(ev.AsSpan(6));
        Assert.Equal(InstanceVillage.EventCheckRewardIconVisibility, Encoding.UTF8.GetString(ev, 8, evLen));

        // S→C 0x02c9 subtag 0x03 PortQuotas: verifies critical bits[0..27]/[28..55] wire swap
        var quota = new InstanceVillage.PortQuotaSlot(ExportRow: 5, Required: 1234, Delivered: 567);
        byte[] quotaPkt = InstanceVillage.PortQuotas(2, [quota]);
        Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(quotaPkt));
        Assert.Equal(InstanceVillage.RulesetRow, quotaPkt[2]);
        // payloadLen = 1 (subtag) + 1 (portIdx) + 3*8 = 26 (< 0x80 -> 1 byte compact uint)
        Assert.Equal(26, quotaPkt[3]);
        Assert.Equal((byte)InstanceVillage.ServerOp.PortQuotas, quotaPkt[4]);
        Assert.Equal(2, quotaPkt[5]);
        ulong packed = BinaryPrimitives.ReadUInt64LittleEndian(quotaPkt.AsSpan(6));
        Assert.Equal(5ul, (packed >> 56) & 0x0f);
        Assert.Equal(1234ul, (packed >> 28) & 0x0fffffff);
        Assert.Equal(567ul, packed & 0x0fffffff);

        // S→C 0x02c9 subtag 0x04 FullVillageState + outer tail serverNowSeconds
        var snap = new InstanceVillage.VillageStateSnapshot(
            Resources: Enumerable.Range(1, 21).Select(i => (uint)i).ToArray(),
            ProductionProgress: Enumerable.Range(100, 15).Select(i => (uint)i).ToArray(),
            GoldInTreasury: 50_000,
            GoldBurnRemainderOrTicks: 42,
            FarmPlotCropResource: [10, 11, 12, 13, 14, InstanceVillage.EmptyFarmPlot],
            ShipTimers: [new(10, 100), new(0, 0), new(5, 50)],
            MapDevices: [new(1, 20, 0x01, 0x02, [100, 50]), new(0, 0, 0, 0, []), new(0, 0, 0, 0, [])],
            DisenchantTimer: new(3, 60),
            NibbleLo: 2,
            NibbleHi: 1,
            StateTimestamp: 1_700_000_000ul,
            ShipKidnapSlot1Based: 1,
            ShipKidnapFlag: 1,
            MapKidnapSlot1Based: 2,
            MapKidnapFlag: 0,
            ServerNowSeconds: 1_700_000_123ul);
        byte[] statePkt = InstanceVillage.FullVillageState(snap);
        Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(statePkt));
        Assert.Equal(InstanceVillage.RulesetRow, statePkt[2]);
        // Trailing 8 bytes must be serverNowSeconds LE
        Assert.Equal(1_700_000_123ul, BinaryPrimitives.ReadUInt64LittleEndian(statePkt.AsSpan(statePkt.Length - 8)));

        // C→S 0x02ca subtag 0x06 SendShipment (68 bytes payload incl. subtag)
        var shipPayload = new BlobWriter().U8((byte)InstanceVillage.ClientOp.SendShipment).U8(1).U8(4).U8(0xff);
        for (uint i = 0; i < 16; i++) shipPayload.U32(i * 10);
        byte[] shipBytes = shipPayload.ToArray();
        Assert.Equal(68, shipBytes.Length);
        Assert.True(InstanceVillage.TryReadCommand(shipBytes, out var shipCmd));
        var ship = Assert.IsType<InstanceVillage.SendShipmentCommand>(shipCmd);
        Assert.Equal((1, 4, 0xff), (ship.ShipIndex, ship.PortRow, ship.FailureRow));
        Assert.Equal(150u, ship.ExportAmounts[15]);

        // C→S 0x02ca subtag 0x14 TakeRewardToSlot exact field order
        byte[] takeSlotBytes = new BlobWriter()
            .U8((byte)InstanceVillage.ClientOp.TakeRewardToSlot)
            .U32(1).U32(42).U32(1).U32(3).U32(4).U32(9).ToArray();
        Assert.True(InstanceVillage.TryReadCommand(takeSlotBytes, out var takeSlotCmd));
        var takeSlot = Assert.IsType<InstanceVillage.TakeRewardToSlotCommand>(takeSlotCmd);
        Assert.Equal((1u, 42u, 1u, 3u, 4u, 9u), (takeSlot.U8Param, takeSlot.ItemId, takeSlot.DstInventoryId, takeSlot.X, takeSlot.Y, takeSlot.Extra));

        // Recombinator codecs: C→S 0x028f (mode 0 = Predictable, mode 1 = Unpredictable), S→C 0x0293, S→C 0x0291 (u16le!), C→S 0x010b
        byte[] recBytes = new LoginWriter().U16(0x028f).U8(0).U8(2).U8(1).U8(3).U8(1).U8(0).U8(0).U8(1).ToArray();
        var rec = InstanceVillage.ReadRecombinate(recBytes);
        Assert.Equal([1, 3], rec.Slot0SelectedModIndices);
        Assert.Equal([0], rec.Slot1SelectedModIndices);
        Assert.True(rec.PredictableMode);
        Assert.True(rec.Execute);

        byte[] recUnpredBytes = new LoginWriter().U16(0x028f).U8(0).U8(0).U8(0).U8(1).U8(0).ToArray();
        var recUnpred = InstanceVillage.ReadRecombinate(recUnpredBytes);
        Assert.False(recUnpred.PredictableMode);
        Assert.False(recUnpred.Execute);

        byte[] chance = InstanceVillage.RecombinationChance(4275, mode: 1);
        Assert.Equal([0x02, 0x93, 0x01, 0x00, 0x00, 0x10, 0xb3], chance);

        byte[] result = InstanceVillage.RecombinationResult(success: true, errorOrStatusCode: 0x0102);
        Assert.Equal([0x02, 0x91, 0x01, 0x02, 0x01], result);

        byte[] qmBytes = new LoginWriter().U16(0x010b).U32(0x11223344u).U8(0).U8(1).U8(0).ToArray();
        var qm = InstanceVillage.ReadRecombinatorQuickMove(qmBytes);
        Assert.Equal(0x11223344u, qm.ItemId);
        Assert.True(qm.IntoRecombinator);
        Assert.False(qm.IsOutputInventory);
    }
}
