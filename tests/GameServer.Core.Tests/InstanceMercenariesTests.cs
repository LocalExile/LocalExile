using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;
using Xunit;

namespace GameServer.Core.Tests;

public class InstanceMercenariesTests
{
    static InstanceMercenaries.MercenaryBaseRecord SampleBase(byte level = 72) => new(
        BuildHash16: 0x1a2b,
        Level: level,
        FirstNameHash32: 0x11223344,
        SecondNameHash32: 0x55667788,
        FlavourHash16: 0x3c4d,
        Skills:
        [
            new(0x0102, [0x0304, 0x0506]),
            new(0x0708, []),
        ]);

    [Fact]
    public void Encounter_0xa1_wire_payloads_match_binary_layout_and_decode_c2s_actions()
    {
        // Sub 0: u8 0, u8 attributesMask, u8 count, count * 7B (u16 LE visual, u32 LE mtx, u8 flag).
        byte[] visPacket = InstanceMercenaries.EncounterVisuals(0x05, [
            new(0x1234, 0x9abcdef0, 2),
            new(0x5678, 0x00000001, 0),
        ]);
        var (visRow, visBody) = InstanceRulesets.ReadAction([0x02, 0xca, .. visPacket[2..]]);
        Assert.Equal(InstanceMercenaries.EncounterRulesetRow, visRow);
        Assert.Equal(3 + 2 * 7, visBody.Length);
        Assert.Equal((byte)0, visBody[0]);
        Assert.Equal((byte)0x05, visBody[1]);
        Assert.Equal((byte)2, visBody[2]);
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(visBody.AsSpan(3, 2)));
        Assert.Equal(0x9abcdef0u, BinaryPrimitives.ReadUInt32LittleEndian(visBody.AsSpan(5, 4)));
        Assert.Equal((byte)2, visBody[9]);
        Assert.Equal((ushort)0x5678, BinaryPrimitives.ReadUInt16LittleEndian(visBody.AsSpan(10, 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(visBody.AsSpan(12, 4)));
        Assert.Equal((byte)0, visBody[16]);

        // Sub 1: BaseMercenaryRecord + u32 LE wager + u8 isWarrant + u32 LE gearInvId + u32 LE rucksackInvId.
        var baseRec = SampleBase(83);
        byte[] details = InstanceMercenaries.EncounterDetailsPayload(baseRec, wagerCost: 2450, isWarrantCall: true,
            gearInventoryId: 500, rucksackInventoryId: 501);
        Assert.Equal((byte)1, details[0]);
        int off = 1;
        Assert.True(InstanceMercenaries.TryReadBaseRecord(details, ref off, out var decoded));
        Assert.Equal(baseRec.BuildHash16, decoded.BuildHash16);
        Assert.Equal(baseRec.Level, decoded.Level);
        Assert.Equal(baseRec.FirstNameHash32, decoded.FirstNameHash32);
        Assert.Equal(baseRec.SecondNameHash32, decoded.SecondNameHash32);
        Assert.Equal(baseRec.FlavourHash16, decoded.FlavourHash16);
        Assert.Equal(2, decoded.Skills.Count);
        Assert.Equal((ushort)0x0102, decoded.Skills[0].SkillHash16);
        Assert.Equal([0x0304, 0x0506], decoded.Skills[0].SupportHashes16);
        Assert.Empty(decoded.Skills[1].SupportHashes16);
        Assert.Equal(2450u, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(off, 4)));
        Assert.Equal((byte)1, details[off + 4]);
        Assert.Equal(500u, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(off + 5, 4)));
        Assert.Equal(501u, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(off + 9, 4)));
        Assert.Equal(details.Length, off + 13);

        // Sub 2 (opt-in) & Sub 3 (wager update).
        Assert.Equal([2, 1], InstanceMercenaries.EncounterOptInPayload(true));
        Assert.Equal([2, 0], InstanceMercenaries.EncounterOptInPayload(false));
        Assert.Equal([3, 0x39, 0x05, 0x00, 0x00], InstanceMercenaries.EncounterWagerCostPayload(1337));

        // C->S 0x2ca payloads for ruleset 0xa1.
        Assert.True(InstanceMercenaries.TryReadEncounterAction([1, 1, 0, 0, 0x78, 0x56, 0x34, 0x12], out var takeGear));
        Assert.Equal(new InstanceMercenaries.EncounterAction(InstanceMercenaries.EncounterActionKind.TakeGearItem,
            ShouldRecruit: true, ItemId: 0x12345678), takeGear);

        // Rucksack pick is 4 bytes and omits the trailing u32 item id.
        Assert.True(InstanceMercenaries.TryReadEncounterAction([1, 0, 0, 1], out var takeRuck));
        Assert.Equal(new InstanceMercenaries.EncounterAction(InstanceMercenaries.EncounterActionKind.TakeRucksack,
            ShouldRecruit: false), takeRuck);

        // Rematch matches retail 8-byte DuelChoice payload.
        Assert.True(InstanceMercenaries.TryReadEncounterAction([1, 1, 1, 0, 0, 0, 0, 0], out var rematch));
        Assert.Equal(new InstanceMercenaries.EncounterAction(InstanceMercenaries.EncounterActionKind.Rematch,
            ShouldRecruit: true), rematch);

        Assert.True(InstanceMercenaries.TryReadEncounterAction([2, 1], out var hireJoin));
        Assert.Equal(new InstanceMercenaries.EncounterAction(InstanceMercenaries.EncounterActionKind.Hire,
            JoinImmediately: true), hireJoin);

        Assert.True(InstanceMercenaries.TryReadEncounterAction([2, 0], out var hireReserve));
        Assert.Equal(new InstanceMercenaries.EncounterAction(InstanceMercenaries.EncounterActionKind.Hire,
            JoinImmediately: false), hireReserve);

        Assert.True(InstanceMercenaries.TryReadEncounterAction([3], out var optIn));
        Assert.Equal(new InstanceMercenaries.EncounterAction(InstanceMercenaries.EncounterActionKind.OptIn), optIn);

        // Truncated or unknown payloads fail cleanly.
        Assert.False(InstanceMercenaries.TryReadEncounterAction([], out _));
        Assert.False(InstanceMercenaries.TryReadEncounterAction([1, 0, 0, 0, 0x01], out _));
        Assert.False(InstanceMercenaries.TryReadEncounterAction([1, 0, 2, 0], out _));
        Assert.False(InstanceMercenaries.TryReadEncounterAction([2], out _));
        Assert.False(InstanceMercenaries.TryReadEncounterAction([4], out _));
    }

    [Fact]
    public void Area_0xa2_snapshot_sorts_records_avoids_sub4_bug_and_decodes_c2s_actions()
    {
        var r2 = new InstanceMercenaries.MercenaryRosterRecord(SampleBase(80), Key: 2, ReviveCost: 5000, Dead: true);
        var r0 = new InstanceMercenaries.MercenaryRosterRecord(SampleBase(68), Key: 0, ReviveCost: 1500, Dead: false);
        var r3 = new InstanceMercenaries.MercenaryRosterRecord(SampleBase(75), Key: 3, ReviveCost: 0, Dead: false);

        // Out-of-order input must be emitted in ascending key order (client binary-searches by key).
        byte[] snap = InstanceMercenaries.AreaRosterSnapshotPayload([r2, r3, r0], [(1, 2), (3, 4)]);
        Assert.Equal((byte)0, snap[0]);
        Assert.Equal((byte)3, snap[1]);
        int off = 2;
        Assert.True(InstanceMercenaries.TryReadRosterRecord(snap, ref off, out var first));
        Assert.True(InstanceMercenaries.TryReadRosterRecord(snap, ref off, out var second));
        Assert.True(InstanceMercenaries.TryReadRosterRecord(snap, ref off, out var third));
        Assert.Equal([0, 2, 3], new[] { first.Key, second.Key, third.Key });
        Assert.Equal((1500u, false), (first.ReviveCost, first.Dead));
        Assert.Equal((5000u, true), (second.ReviveCost, second.Dead));
        Assert.Equal((byte)2, snap[off]);
        Assert.Equal([1, 2, 3, 4], snap[(off + 1)..]);

        // Empty snapshot is [0, 0, 0].
        Assert.Equal([0, 0, 0], InstanceMercenaries.AreaRosterSnapshotPayload([]));

        // AreaReplacePackets must emit sub 1 (Remove) + sub 3 (Insert) and never sub 4 (retail client bug).
        byte[][] replace = InstanceMercenaries.AreaReplacePackets(r0);
        Assert.Equal(2, replace.Length);
        var (remRow, remPayload) = InstanceRulesets.ReadAction([0x02, 0xca, .. replace[0][2..]]);
        var (insRow, insPayload) = InstanceRulesets.ReadAction([0x02, 0xca, .. replace[1][2..]]);
        Assert.Equal(InstanceMercenaries.AreaRulesetRow, remRow);
        Assert.Equal(InstanceMercenaries.AreaRulesetRow, insRow);
        Assert.Equal([1, 0], remPayload);
        Assert.Equal((byte)3, insPayload[0]);
        int insOff = 1;
        Assert.True(InstanceMercenaries.TryReadRosterRecord(insPayload, ref insOff, out var insRec));
        Assert.Equal((byte)0, insRec.Key);
        Assert.Equal(insPayload.Length, insOff);

        // Subs 2, 5, 6.
        Assert.Equal([2, 1], InstanceMercenaries.AreaSetAlivePayload(1));
        Assert.Equal([5, 1], InstanceMercenaries.AreaSetDeadPayload(1));
        Assert.Equal([6, 0], InstanceMercenaries.AreaSetInvitedPayload(null));
        Assert.Equal([6, 1, 2], InstanceMercenaries.AreaSetInvitedPayload(2));

        // C->S 0x2ca payloads for ruleset 0xa2.
        Assert.True(InstanceMercenaries.TryReadAreaAction([0, 1], out var invite));
        Assert.Equal(new InstanceMercenaries.AreaAction(InstanceMercenaries.AreaActionKind.Invite, 1), invite);

        Assert.True(InstanceMercenaries.TryReadAreaAction([1], out var reserve));
        Assert.Equal(new InstanceMercenaries.AreaAction(InstanceMercenaries.AreaActionKind.Reserve), reserve);

        Assert.True(InstanceMercenaries.TryReadAreaAction([2, 2], out var revive));
        Assert.Equal(new InstanceMercenaries.AreaAction(InstanceMercenaries.AreaActionKind.Revive, 2), revive);

        Assert.True(InstanceMercenaries.TryReadAreaAction([3, 0], out var dismiss));
        Assert.Equal(new InstanceMercenaries.AreaAction(InstanceMercenaries.AreaActionKind.Dismiss, 0), dismiss);

        Assert.False(InstanceMercenaries.TryReadAreaAction([], out _));
        Assert.False(InstanceMercenaries.TryReadAreaAction([0], out _));
        Assert.False(InstanceMercenaries.TryReadAreaAction([2], out _));
        Assert.False(InstanceMercenaries.TryReadAreaAction([3], out _));
        Assert.False(InstanceMercenaries.TryReadAreaAction([4, 0], out _));
    }

    [Fact]
    public void Mercenary_quick_move_0x010c_open_ui_and_inventory_slots_match_spec()
    {
        // C->S 0x010c (9B BE): [0x010c][itemId:u32 BE][fromExpanded:u8][toMercenary:u8][target:u8].
        byte[] intoMerc = [0x01, 0x0c, 0x00, 0x00, 0x00, 0x2a, 0x01, 0x01, 0x02];
        byte[] outOfMerc = [0x01, 0x0c, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 96];
        var framed = InstanceProtocol.SplitClientMessages([.. intoMerc, .. outOfMerc], out var partial, out var unknown);
        Assert.Empty(partial);
        Assert.Empty(unknown);
        Assert.Equal(2, framed.Count);
        Assert.All(framed, m => Assert.Equal(InstanceItems.MercenaryQuickMoveId, m.Id));

        var mIn = InstanceItems.ReadMercenaryQuickMove(framed[0].Bytes);
        Assert.Equal(new InstanceItems.MercenaryQuickMove(0x2au, FromExpanded: true, ToMercenary: true, Target: 2), mIn);

        var mOut = InstanceItems.ReadMercenaryQuickMove(framed[1].Bytes);
        Assert.Equal(new InstanceItems.MercenaryQuickMove(0x100u, FromExpanded: false, ToMercenary: false, Target: 96), mOut);

        // S->C 0x007b OpenUi for MercenaryLoadout (0x60) and EncounterMercenaryLoadout (0x61).
        byte[] openLoadout = InstanceWaypoints.OpenUi(InstanceWaypoints.MercenaryLoadoutUi, 0x80000005u, 1u);
        Assert.Equal(19, openLoadout.Length);
        Assert.Equal(InstanceWaypoints.OpenUiId, BinaryPrimitives.ReadUInt16BigEndian(openLoadout.AsSpan(0, 2)));
        Assert.Equal(InstanceWaypoints.MercenaryLoadoutUi, openLoadout[2]);
        Assert.Equal(0x80000005u, BinaryPrimitives.ReadUInt32BigEndian(openLoadout.AsSpan(3, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(openLoadout.AsSpan(7, 4)));

        byte[] openEncounter = InstanceWaypoints.OpenUi(InstanceWaypoints.EncounterMercenaryLoadoutUi, 0x80000009u);
        Assert.Equal(19, openEncounter.Length);
        Assert.Equal(InstanceWaypoints.EncounterMercenaryLoadoutUi, openEncounter[2]);
        Assert.Equal(0x80000009u, BinaryPrimitives.ReadUInt32BigEndian(openEncounter.AsSpan(3, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(openEncounter.AsSpan(7, 4)));

        // PlayerDefaults rows 95..97 & 107 are 17x4 kind 0x3c; encounter manager is 1.
        foreach (byte row in new byte[] { 95, 96, 97, 107 })
        {
            var slot = InstanceInventories.PlayerDefaults[row];
            Assert.Equal((row, (byte)0x3c, (byte)17, (byte)4), (slot.Row, slot.Kind, slot.Width, slot.Height));
        }
        Assert.Equal((byte)1, InstanceMercenaries.EncounterInventoryManager);
        Assert.Equal(((byte)107, (byte)0x3c, (byte)17, (byte)4), (InstanceMercenaries.EncounterGearSlot.Row,
            InstanceMercenaries.EncounterGearSlot.Kind, InstanceMercenaries.EncounterGearSlot.Width, InstanceMercenaries.EncounterGearSlot.Height));
        Assert.Equal(((byte)83, (byte)0x0f, (byte)2, (byte)2), (InstanceMercenaries.EncounterRucksackSlot.Row,
            InstanceMercenaries.EncounterRucksackSlot.Kind, InstanceMercenaries.EncounterRucksackSlot.Width, InstanceMercenaries.EncounterRucksackSlot.Height));
    }
}
