using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Wire framing for Mercenaries of Trarthus rulesets <c>0xa1</c> (<c>MercenaryEncounterRules</c>, decoder
/// <c>0x141733150</c>) and <c>0xa2</c> (<c>MercenaryAreaRules</c>, decoder <c>0x141732850</c>). All fields inside
/// ruleset payloads are little-endian and all table references use FNV-1a <c>HASH16</c>/<c>HASH32</c> keys
/// (<c>0x142480640</c>, validated by <c>0x142480290</c>).
/// </summary>
public static class InstanceMercenaries
{
    /// <summary><c>Rulesets.dat</c> row 161 (<c>MercenaryEncounterRules</c>).</summary>
    public const byte EncounterRulesetRow = 0xa1;

    /// <summary><c>Rulesets.dat</c> row 162 (<c>MercenaryAreaRules</c>).</summary>
    public const byte AreaRulesetRow = 0xa2;

    /// <summary>S→C <c>0x00c0</c> inventory manager for encounter gear and rucksack inventories (<c>instance + 0xca10</c>,
    /// the same manager NPC shops and offers use).</summary>
    public const byte EncounterInventoryManager = 1;

    /// <summary>Slot descriptor for an encounter mercenary's gear inventory in <see cref="EncounterInventoryManager"/>.</summary>
    public static readonly InstanceInventories.Slot EncounterGearSlot = new(107, 0x3c, 17, 4);

    /// <summary>Slot descriptor for an encounter mercenary's optional 2×2 rucksack inventory in <see cref="EncounterInventoryManager"/>.</summary>
    public static readonly InstanceInventories.Slot EncounterRucksackSlot = new(83, 0x0f, 2, 2);

    /// <summary>One active skill entry inside <see cref="MercenaryBaseRecord"/>: <c>MercenarySkills.HASH16</c> and its
    /// linked <c>MercenarySupports.HASH16</c> list.</summary>
    public sealed record MercenarySkillWire(ushort SkillHash16, IReadOnlyList<ushort> SupportHashes16)
    {
        public IReadOnlyList<ushort> SupportHashes16 { get; init => field = Owned.List(value); } = Owned.List(SupportHashes16);
    }

    /// <summary>Common base record (<c>0x142480640</c>, little-endian) shared by ruleset <c>0xa1</c> sub 1 and
    /// ruleset <c>0xa2</c> subs 0, 3, and 4.</summary>
    public sealed record MercenaryBaseRecord(
        ushort BuildHash16,
        byte Level,
        uint FirstNameHash32,
        uint SecondNameHash32,
        ushort FlavourHash16,
        IReadOnlyList<MercenarySkillWire> Skills)
    {
        public IReadOnlyList<MercenarySkillWire> Skills { get; init => field = Owned.List(value); } = Owned.List(Skills);
    }

    /// <summary>Full <c>0x70</c>-byte roster record (<c>0x142481320</c>, little-endian) carried by ruleset <c>0xa2</c>:
    /// <see cref="Base"/>, slot <see cref="Key"/> (<c>0..2</c> permanent <c>Mercenary1..3</c>, <c>3</c> temporary
    /// <c>MercenaryTemporary</c>), gold <see cref="ReviveCost"/>, and <see cref="Dead"/> flag.</summary>
    public sealed record MercenaryRosterRecord(MercenaryBaseRecord Base, byte Key, uint ReviveCost, bool Dead);

    /// <summary>One 7-byte visual override entry in ruleset <c>0xa1</c> sub 0 (<c>0x1424fab70</c>):
    /// <c>ItemVisualIdentity.HASH16</c>, <c>MTXTypeGameSpecific.HASH32</c>, and tier/flag byte.</summary>
    public readonly record struct MercenaryVisualOverride(ushort VisualHash16, uint MtxHash32 = 0, byte Flag = 0);

    // ---- S→C 0x02c9 ruleset 0xa1 (MercenaryEncounterRules) ------------------------------------------------------------

    /// <summary>Ruleset <c>0xa1</c> sub 0 payload: <c>u8 0, u8 attributesMask, u8 count, count × 7B visual entry</c>.</summary>
    public static byte[] EncounterVisualsPayload(byte attributesMask, IReadOnlyList<MercenaryVisualOverride> visuals)
    {
        int count = Math.Min(visuals.Count, byte.MaxValue);
        var w = new BlobWriter().U8(0).U8(attributesMask).U8((byte)count);
        for (int i = 0; i < count; i++)
        {
            var v = visuals[i];
            w.U16(v.VisualHash16).U32(v.MtxHash32).U8(v.Flag);
        }
        return w.ToArray();
    }

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa1</c> sub 0.</summary>
    public static byte[] EncounterVisuals(byte attributesMask, IReadOnlyList<MercenaryVisualOverride> visuals) =>
        InstanceRulesets.Data(EncounterRulesetRow, EncounterVisualsPayload(attributesMask, visuals));

    /// <summary>Ruleset <c>0xa1</c> sub 1 payload: <c>u8 1</c>, <see cref="MercenaryBaseRecord"/>, <c>u32 LE wagerCost</c>,
    /// <c>u8 isWarrantCall</c>, <c>u32 LE gearInventoryId</c>, <c>u32 LE rucksackInventoryId</c>.</summary>
    public static byte[] EncounterDetailsPayload(
        MercenaryBaseRecord record,
        uint wagerCost,
        bool isWarrantCall,
        uint gearInventoryId,
        uint rucksackInventoryId = 0)
    {
        var w = new BlobWriter().U8(1);
        WriteBaseRecord(w, record);
        w.U32(wagerCost).U8((byte)(isWarrantCall ? 1 : 0)).U32(gearInventoryId).U32(rucksackInventoryId);
        return w.ToArray();
    }

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa1</c> sub 1.</summary>
    public static byte[] EncounterDetails(
        MercenaryBaseRecord record,
        uint wagerCost,
        bool isWarrantCall,
        uint gearInventoryId,
        uint rucksackInventoryId = 0) =>
        InstanceRulesets.Data(EncounterRulesetRow,
            EncounterDetailsPayload(record, wagerCost, isWarrantCall, gearInventoryId, rucksackInventoryId));

    /// <summary>Ruleset <c>0xa1</c> sub 2 payload: <c>u8 2, u8 optedIn</c>.</summary>
    public static byte[] EncounterOptInPayload(bool optedIn) => [2, (byte)(optedIn ? 1 : 0)];

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa1</c> sub 2.</summary>
    public static byte[] EncounterOptIn(bool optedIn) =>
        InstanceRulesets.Data(EncounterRulesetRow, EncounterOptInPayload(optedIn));

    /// <summary>Ruleset <c>0xa1</c> sub 3 payload: <c>u8 3, u32 LE wagerCost</c>.</summary>
    public static byte[] EncounterWagerCostPayload(uint wagerCost) =>
        new BlobWriter().U8(3).U32(wagerCost).ToArray();

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa1</c> sub 3.</summary>
    public static byte[] EncounterWagerCost(uint wagerCost) =>
        InstanceRulesets.Data(EncounterRulesetRow, EncounterWagerCostPayload(wagerCost));

    // ---- S→C 0x02c9 ruleset 0xa2 (MercenaryAreaRules) -----------------------------------------------------------------

    /// <summary>Ruleset <c>0xa2</c> sub 0 payload (full snapshot). Records are sorted ascending by
    /// <see cref="MercenaryRosterRecord.Key"/> as required by the client's binary search (<c>0x141732ff0</c>).</summary>
    public static byte[] AreaRosterSnapshotPayload(
        IReadOnlyList<MercenaryRosterRecord> records,
        IReadOnlyList<(byte A, byte B)>? exiledPairs = null)
    {
        var sorted = records.OrderBy(r => r.Key).Take(byte.MaxValue).ToArray();
        var w = new BlobWriter().U8(0).U8((byte)sorted.Length);
        foreach (var r in sorted) WriteRosterRecord(w, r);
        int pairCount = Math.Min(exiledPairs?.Count ?? 0, byte.MaxValue);
        w.U8((byte)pairCount);
        for (int i = 0; i < pairCount; i++) w.U8(exiledPairs![i].A).U8(exiledPairs[i].B);
        return w.ToArray();
    }

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa2</c> sub 0 (full roster snapshot).</summary>
    public static byte[] AreaRosterSnapshot(
        IReadOnlyList<MercenaryRosterRecord> records,
        IReadOnlyList<(byte A, byte B)>? exiledPairs = null) =>
        InstanceRulesets.Data(AreaRulesetRow, AreaRosterSnapshotPayload(records, exiledPairs));

    /// <summary>Ruleset <c>0xa2</c> sub 1 payload: remove record by key (<c>u8 1, u8 key</c>).</summary>
    public static byte[] AreaRemovePayload(byte key) => [1, key];

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa2</c> sub 1 (remove).</summary>
    public static byte[] AreaRemove(byte key) => InstanceRulesets.Data(AreaRulesetRow, AreaRemovePayload(key));

    /// <summary>Ruleset <c>0xa2</c> sub 2 payload: mark key alive/revived (<c>u8 2, u8 key</c>).</summary>
    public static byte[] AreaSetAlivePayload(byte key) => [2, key];

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa2</c> sub 2 (revived).</summary>
    public static byte[] AreaSetAlive(byte key) => InstanceRulesets.Data(AreaRulesetRow, AreaSetAlivePayload(key));

    /// <summary>Ruleset <c>0xa2</c> sub 3 payload: insert a record in sorted key order.</summary>
    public static byte[] AreaInsertPayload(MercenaryRosterRecord record)
    {
        var w = new BlobWriter().U8(3);
        WriteRosterRecord(w, record);
        return w.ToArray();
    }

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa2</c> sub 3 (insert).</summary>
    public static byte[] AreaInsert(MercenaryRosterRecord record) =>
        InstanceRulesets.Data(AreaRulesetRow, AreaInsertPayload(record));

    /// <summary>Safe record replacement as sub 1 (Remove) + sub 3 (Insert). Retail 3.29.3c/d sub 4 has a client bug
    /// at <c>0x141732d3a</c> that overwrites the last vector element instead of the matched key.</summary>
    public static byte[][] AreaReplacePackets(MercenaryRosterRecord record) =>
        [AreaRemove(record.Key), AreaInsert(record)];

    /// <summary>Ruleset <c>0xa2</c> sub 5 payload: mark key dead (<c>u8 5, u8 key</c>).</summary>
    public static byte[] AreaSetDeadPayload(byte key) => [5, key];

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa2</c> sub 5 (dead).</summary>
    public static byte[] AreaSetDead(byte key) => InstanceRulesets.Data(AreaRulesetRow, AreaSetDeadPayload(key));

    /// <summary>Ruleset <c>0xa2</c> sub 6 payload: <c>[6, 0]</c> for none invited, or <c>[6, 1, key]</c>.</summary>
    public static byte[] AreaSetInvitedPayload(byte? invitedKey) =>
        invitedKey is byte key ? [6, 1, key] : [6, 0];

    /// <summary>S→C <c>0x02c9</c> ruleset <c>0xa2</c> sub 6 (invited state).</summary>
    public static byte[] AreaSetInvited(byte? invitedKey) =>
        InstanceRulesets.Data(AreaRulesetRow, AreaSetInvitedPayload(invitedKey));

    // ---- C→S 0x02ca action decoders -----------------------------------------------------------------------------------

    public enum EncounterActionKind : byte { TakeGearItem, TakeRucksack, Rematch, Hire, OptIn }

    public readonly record struct EncounterAction(
        EncounterActionKind Kind,
        bool ShouldRecruit = false,
        uint ItemId = 0,
        bool JoinImmediately = false);

    /// <summary>Decodes a C→S <c>0x02ca</c> payload targeting ruleset <c>0xa1</c> (<c>MercenaryEncounterRules</c>).</summary>
    public static bool TryReadEncounterAction(ReadOnlySpan<byte> payload, out EncounterAction action)
    {
        action = default;
        if (payload.Length == 0) return false;
        switch (payload[0])
        {
            case 1:
                if (payload.Length < 4) return false;
                bool recruit = payload[1] != 0;
                if (payload[2] == 1)
                {
                    action = new(EncounterActionKind.Rematch, ShouldRecruit: recruit);
                    return true;
                }
                if (payload[2] != 0) return false;
                if (payload[3] != 0)
                {
                    // Extra/rucksack pick omits the trailing u32 item id (4 bytes total, 0x1417401fe).
                    action = new(EncounterActionKind.TakeRucksack, ShouldRecruit: recruit);
                    return true;
                }
                if (payload.Length < 8) return false;
                action = new(EncounterActionKind.TakeGearItem, ShouldRecruit: recruit,
                    ItemId: BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)));
                return true;
            case 2:
                if (payload.Length < 2) return false;
                action = new(EncounterActionKind.Hire, JoinImmediately: payload[1] != 0);
                return true;
            case 3:
                action = new(EncounterActionKind.OptIn);
                return true;
            default:
                return false;
        }
    }

    public enum AreaActionKind : byte { Invite, Reserve, Revive, Dismiss, ShowCurrent, ShowTemporary }

    public readonly record struct AreaAction(AreaActionKind Kind, byte Key = 0);

    /// <summary>Decodes a C→S <c>0x02ca</c> payload targeting ruleset <c>0xa2</c> (<c>MercenaryAreaRules</c>).</summary>
    public static bool TryReadAreaAction(ReadOnlySpan<byte> payload, out AreaAction action)
    {
        action = default;
        if (payload.Length == 0) return false;
        switch (payload[0])
        {
            case 0 when payload.Length >= 2:
                action = new(AreaActionKind.Invite, payload[1]);
                return true;
            case 1:
                action = new(AreaActionKind.Reserve);
                return true;
            case 2 when payload.Length >= 2:
                action = new(AreaActionKind.Revive, payload[1]);
                return true;
            case 3 when payload.Length >= 2:
                action = new(AreaActionKind.Dismiss, payload[1]);
                return true;
            case 4:
                action = new(AreaActionKind.ShowCurrent);
                return true;
            case 5:
                action = new(AreaActionKind.ShowTemporary);
                return true;
            default:
                return false;
        }
    }

    // ---- Little-endian record writers & readers -----------------------------------------------------------------------

    public static void WriteBaseRecord(BlobWriter w, MercenaryBaseRecord r)
    {
        w.U16(r.BuildHash16).U8(r.Level).U32(r.FirstNameHash32).U32(r.SecondNameHash32).U16(r.FlavourHash16);
        int skillCount = Math.Min(r.Skills.Count, byte.MaxValue);
        w.U8((byte)skillCount);
        for (int i = 0; i < skillCount; i++)
        {
            var s = r.Skills[i];
            int supportCount = Math.Min(s.SupportHashes16.Count, byte.MaxValue);
            w.U16(s.SkillHash16).U8((byte)supportCount);
            for (int k = 0; k < supportCount; k++) w.U16(s.SupportHashes16[k]);
        }
    }

    public static void WriteRosterRecord(BlobWriter w, MercenaryRosterRecord r)
    {
        WriteBaseRecord(w, r.Base);
        w.U8(r.Key).U32(r.ReviveCost).U8((byte)(r.Dead ? 1 : 0));
    }

    public static bool TryReadBaseRecord(ReadOnlySpan<byte> data, ref int offset, out MercenaryBaseRecord record)
    {
        record = null!;
        if (offset + 14 > data.Length) return false;
        ushort build = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)); offset += 2;
        byte level = data[offset++];
        uint word1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        uint word2 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        ushort flavour = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)); offset += 2;
        byte skillCount = data[offset++];
        var skills = new MercenarySkillWire[skillCount];
        for (int i = 0; i < skillCount; i++)
        {
            if (offset + 3 > data.Length) return false;
            ushort skillHash = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)); offset += 2;
            byte supportCount = data[offset++];
            if (offset + supportCount * 2 > data.Length) return false;
            var supports = new ushort[supportCount];
            for (int k = 0; k < supportCount; k++)
            {
                supports[k] = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
                offset += 2;
            }
            skills[i] = new MercenarySkillWire(skillHash, supports);
        }
        record = new MercenaryBaseRecord(build, level, word1, word2, flavour, skills);
        return true;
    }

    public static bool TryReadRosterRecord(ReadOnlySpan<byte> data, ref int offset, out MercenaryRosterRecord record)
    {
        record = null!;
        if (!TryReadBaseRecord(data, ref offset, out var baseRecord)) return false;
        if (offset + 6 > data.Length) return false;
        byte key = data[offset++];
        uint reviveCost = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        bool dead = data[offset++] != 0;
        record = new MercenaryRosterRecord(baseRecord, key, reviveCost, dead);
        return true;
    }
}
