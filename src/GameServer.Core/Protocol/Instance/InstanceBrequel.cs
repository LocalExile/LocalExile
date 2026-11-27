using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Breach ("Brequel" / Keepers of the Flame) instance protocol messages (3.29.3c/d binary-verified):
/// Hiveblood sync (<c>S→C 0x02d2</c>), Genesis Tree state (<c>S→C 0x02cf</c>), Genesis Tree allocate/refund
/// (<c>C→S 0x02d0</c>/<c>0x02d1</c>), Wombgift fertilise (<c>C→S 0x02d3</c>), <c>BrequelStorage1</c> Ctrl+click
/// quick-move (<c>C→S 0x010d</c>), <c>0x7b</c> UI kinds (<c>0x62</c> Genesis Tree, <c>0x64</c> Wombgift Locker),
/// Rulesets <c>#164</c>/<c>#165</c>/<c>#166</c> (<c>S→C 0x02c9</c>), and encounter skill activation
/// (<c>C→S 0x02ca</c> ruleset <c>165</c>).
/// </summary>
public static class InstanceBrequel
{
    /// <summary>S→C 0x7b kind <c>0x62</c> (98): Genesis Tree (<c>brequel_skill_tree_window</c>, HUD +0xa28, args ignored).</summary>
    public const byte GenesisTreeUi = 0x62;

    /// <summary>S→C 0x7b kind <c>0x64</c> (100): Wombgift Locker (<c>BrequelLocker</c>, HUD +0xd50, arg0 = locker entityId).</summary>
    public const byte WombgiftLockerUi = 0x64;

    /// <summary>Client UI gauge cap for Hiveblood (<c>0x1403bf997</c> / <c>0x1403ce4c3</c>: <c>fill = current * 1e-5f</c>).</summary>
    public const uint MaxHiveblood = 100_000;

    /// <summary>Rulesets.dat rows for Breach encounters: <c>#164</c> ExpandingBrequelEncounterRules,
    /// <c>#165</c> EstablishedBrequelEncounterRules, <c>#166</c> BrequelBarrierRules.</summary>
    public const byte ExpandingEncounterRuleset = 164, EstablishedEncounterRuleset = 165, BarrierRuleset = 166;

    /// <summary>The 4 starting root <c>PassiveSkillGraphId</c>s of <c>ChayulaTreePassiveSkillGraph</c>, which the client
    /// treats as implicitly allocated (<c>0x141ef8510</c>) and which must be excluded from <see cref="TreeState"/>.</summary>
    public static readonly ushort[] RootNodes = [19625, 22476, 42956, 47967];

    /// <summary>S→C 0x02cf: Genesis Tree passive state and per-subtree total allowed points (<c>0x1421b9220</c> → <c>0x141ef6400</c>).</summary>
    public const ushort TreeStateId = 0x02cf;

    /// <summary>S→C 0x02d2: current Hiveblood resource counter (<c>0x1421b9a10</c> → <c>instance + 0xc398</c>).</summary>
    public const ushort HivebloodId = 0x02d2;

    /// <summary>C→S 0x02d0 / 0x02d1 (4 bytes, <c>0x1403cc9a0</c> → <c>0x141f99160</c>): allocate or refund a Genesis Tree node.</summary>
    public const ushort AllocateNodeId = 0x02d0, RefundNodeId = 0x02d1;
    public const int NodeActionLength = 4;

    /// <summary>C→S 0x02d3 (10 bytes, <c>0x1403cacf0</c> → <c>0x141f98f90</c>): fertilise/grow a Wombgift on the Genesis Tree.</summary>
    public const ushort FertiliseWombgiftId = 0x02d3;
    public const int FertiliseWombgiftLength = 10;

    /// <summary>C→S 0x010d (8 bytes, <c>0x14031034a</c> → <c>0x14216ef30</c>): Ctrl+click quick-move between Main/Expanded
    /// inventory and <c>BrequelStorage1</c> (row 101).</summary>
    public const ushort LockerMoveId = 0x010d;
    public const int LockerMoveLength = InstanceItems.LeagueWindowMoveLength;

    /// <summary>S→C 0x7b kind <c>0x62</c>: opens the Genesis Tree window.</summary>
    public static byte[] OpenGenesisTree() => InstanceWaypoints.OpenUi(GenesisTreeUi);

    /// <summary>S→C 0x7b kind <c>0x64</c>: opens the Wombgift Locker bound to <paramref name="lockerEntityId"/>.</summary>
    public static byte[] OpenWombgiftLocker(uint lockerEntityId) => InstanceWaypoints.OpenUi(WombgiftLockerUi, lockerEntityId);

    /// <summary>S→C 0x02d2: sets the client's Hiveblood counter (<c>u16 BE 0x02d2</c> + <c>CompactUInt</c>).</summary>
    public static byte[] Hiveblood(uint currentHiveblood)
    {
        var w = new LoginWriter().U16(HivebloodId);
        InstanceProtocol.CompactUInt(w, currentHiveblood);
        return w.ToArray();
    }

    /// <summary>
    /// S→C 0x02cf: syncs Genesis Tree total allowed (earned) points for the 4 subtrees (<c>BrequelPassiveSubTrees</c> 0..3:
    /// Equipment, Currency, Uniques, Misc) and optionally the full allocated node set (excluding <see cref="RootNodes"/>).
    /// When <paramref name="allocatedNodes"/> is null, <c>pointsOnly = 1</c> keeps the client's existing allocated nodes;
    /// otherwise <c>pointsOnly = 0</c>, <c>u16 BE count</c>, then <c>count × u16 LE</c> node IDs sorted ascending
    /// (required by client <c>std::set_difference</c> at <c>0x141ef6495</c>).
    /// </summary>
    public static byte[] TreeState(uint equipmentPoints, uint currencyPoints, uint uniquePoints, uint miscPoints,
        IEnumerable<ushort>? allocatedNodes = null)
    {
        var w = new LoginWriter().U16(TreeStateId);
        InstanceProtocol.CompactUInt(w, equipmentPoints);
        InstanceProtocol.CompactUInt(w, currencyPoints);
        InstanceProtocol.CompactUInt(w, uniquePoints);
        InstanceProtocol.CompactUInt(w, miscPoints);
        if (allocatedNodes is null)
        {
            w.U8(1);
            return w.ToArray();
        }
        ushort[] sorted = [.. allocatedNodes.Distinct().Order()];
        w.U8(0).U16((ushort)sorted.Length);
        foreach (ushort id in sorted)
            w.U8((byte)(id & 0xff)).U8((byte)(id >> 8));
        return w.ToArray();
    }

    /// <summary>Reads the <c>PassiveSkillGraphId</c> from C→S 0x02d0 (allocate) or 0x02d1 (refund).</summary>
    public static ushort ReadNodeAction(ReadOnlySpan<byte> m) => BinaryPrimitives.ReadUInt16BigEndian(m[2..]);

    /// <summary>C→S 0x02d3: Wombgift fertilise request naming the wire inventory and item id of the Wombgift.</summary>
    public readonly record struct FertiliseWombgift(uint InventoryId, uint ItemId);

    /// <summary>Reads C→S 0x02d3 (10 bytes: <c>u16 BE 0x02d3, u32 BE inventoryId, u32 BE itemId</c>).</summary>
    public static FertiliseWombgift ReadFertiliseWombgift(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), BinaryPrimitives.ReadUInt32BigEndian(m[6..]));

    /// <summary>Reads C→S 0x010d (8 bytes: <c>BrequelStorage1</c> Ctrl+click quick-move).</summary>
    public static InstanceItems.LeagueWindowMove ReadLockerMove(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6] != 0, m[7] != 0);

    /// <summary>Client Hiveblood cost formula (<c>0x1424ad630</c> <c>BrequelFruit::GetHivebloodCost</c>):
    /// <c>(uint)((ulong)graftbloodStorage * fruitMultiplier / 100)</c>.</summary>
    public static uint HivebloodCost(uint graftbloodStorage, uint fruitMultiplier) =>
        (uint)((ulong)graftbloodStorage * fruitMultiplier / 100UL);

    /// <summary>Shared 29-byte <c>ExpandingRadius</c> wire struct (<c>0x141fbe050</c>) used by Rulesets <c>#164</c>
    /// (<c>ExpandingBrequelEncounterRules</c>) and <c>#166</c> (<c>BrequelBarrierRules</c>).</summary>
    public readonly record struct ExpandingRadius(
        float StartRadius,
        float TargetRadius,
        float Duration,
        float Elapsed,
        int SpeedPercent = 0,
        float HoldTimer = 0f,
        float Param14 = 0f,
        byte Flag = 0)
    {
        public const int WireSize = 29;

        public void Write(BlobWriter w) => w
            .F32(StartRadius)
            .F32(TargetRadius)
            .F32(Duration)
            .F32(Elapsed)
            .I32(SpeedPercent)
            .F32(HoldTimer)
            .F32(Param14)
            .U8(Flag);
    }

    /// <summary>State values for <c>EstablishedBrequelEncounterRules</c> (<c>Rulesets#165</c>) sub-opcode 0 (<c>+0x194</c>).</summary>
    public enum EstablishedEncounterState : uint
    {
        Inactive = 0,
        Active = 1,
        FortressEscape = 2,
        Succeeded = 3,
        Ended = 4,
        Failed = 5,
    }

    /// <summary>S→C 0x02c9 payload for <c>ExpandingBrequelEncounterRules</c> (<c>Rulesets#164</c>, 44B):
    /// <c>u8 0, u8 breachIndex, i32 LE centerX, i32 LE centerY, u8 state, ExpandingRadius (29B), f32 LE maxRadius</c>.</summary>
    public static byte[] ExpandingEncounter(byte breachIndex, int centerX, int centerY, byte state,
        ExpandingRadius radius, float maxRadius = 150f)
    {
        var w = new BlobWriter().U8(0).U8(breachIndex).I32(centerX).I32(centerY).U8(state);
        radius.Write(w);
        w.F32(maxRadius);
        return InstanceRulesets.Data(ExpandingEncounterRuleset, w.ToArray());
    }

    /// <summary>S→C 0x02c9 for <c>EstablishedBrequelEncounterRules</c> (<c>Rulesets#165</c>) sub-opcode 0 (14B):
    /// encounter state, grid center (HUD shown within 210 grid units), and sky-fortress flag.</summary>
    public static byte[] EstablishedState(EstablishedEncounterState state, int centerX, int centerY, bool isSkyFortress = false) =>
        InstanceRulesets.Data(EstablishedEncounterRuleset,
            new BlobWriter().U8(0).U32((uint)state).I32(centerX).I32(centerY).U8((byte)(isSkyFortress ? 1 : 0)).ToArray());

    /// <summary>S→C 0x02c9 for <c>EstablishedBrequelEncounterRules</c> (<c>Rulesets#165</c>) sub-opcode 1 (3B):
    /// wave counter (<c>"Wave {currentWave}/{totalWaves}"</c>; hidden when <paramref name="totalWaves"/> is 0).</summary>
    public static byte[] EstablishedWave(byte currentWave, byte totalWaves) =>
        InstanceRulesets.Data(EstablishedEncounterRuleset, [1, currentWave, totalWaves]);

    /// <summary>S→C 0x02c9 for <c>EstablishedBrequelEncounterRules</c> (<c>Rulesets#165</c>) sub-opcode 2 (10B):
    /// Ailith defence bar (<c>currentHits</c>/<c>maxHits</c> and shield overlay flag).</summary>
    public static byte[] EstablishedDefence(int currentHits, int maxHits, bool isShielded = false) =>
        InstanceRulesets.Data(EstablishedEncounterRuleset,
            new BlobWriter().U8(2).I32(currentHits).I32(maxHits).U8((byte)(isShielded ? 1 : 0)).ToArray());

    /// <summary>S→C 0x02c9 for <c>EstablishedBrequelEncounterRules</c> (<c>Rulesets#165</c>) sub-opcode 3 (2B):
    /// Hive atmosphere/overlay blend target (0 or 1).</summary>
    public static byte[] EstablishedOverlay(bool active) =>
        InstanceRulesets.Data(EstablishedEncounterRuleset, [3, (byte)(active ? 1 : 0)]);

    /// <summary>S→C 0x02c9 for <c>BrequelBarrierRules</c> (<c>Rulesets#166</c>) sub-opcode 0 (38B):
    /// barrier center and expanding radius.</summary>
    public static byte[] BarrierRadius(int centerX, int centerY, ExpandingRadius radius)
    {
        var w = new BlobWriter().U8(0).I32(centerX).I32(centerY);
        radius.Write(w);
        return InstanceRulesets.Data(BarrierRuleset, w.ToArray());
    }

    /// <summary>S→C 0x02c9 for <c>BrequelBarrierRules</c> (<c>Rulesets#166</c>) sub-opcode 1 (2B):
    /// barrier visibility/fade target (0 or 1).</summary>
    public static byte[] BarrierActive(bool active) =>
        InstanceRulesets.Data(BarrierRuleset, [1, (byte)(active ? 1 : 0)]);

    /// <summary>Parses the 5-byte <c>EstablishedBrequelEncounterRules</c> (<c>#165</c>) C→S 0x02ca payload
    /// (<c>u8 0, u32 LE skillEntityId</c>, sent by clicking a <c>BrequelSkillObject</c> widget or pressing
    /// <c>ClientLeagueAction#44</c>).</summary>
    public static bool TryReadActivateEncounterSkillPayload(ReadOnlySpan<byte> payload, out uint skillEntityId)
    {
        if (payload.Length >= 5 && payload[0] == 0)
        {
            skillEntityId = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
            return true;
        }
        skillEntityId = 0;
        return false;
    }

    /// <summary>Parses a full C→S 0x02ca <c>BrequelActivateEncounterSkill</c> packet (9 bytes:
    /// <c>02 ca a5 05 00 &lt;skillEntityId:u32 LE&gt;</c>).</summary>
    public static bool TryReadActivateEncounterSkill(ReadOnlySpan<byte> message, out uint skillEntityId)
    {
        skillEntityId = 0;
        if (message.Length < 4 || BinaryPrimitives.ReadUInt16BigEndian(message) != InstanceRulesets.ActionId)
            return false;
        var (row, payload) = InstanceRulesets.ReadAction(message);
        return row == EstablishedEncounterRuleset && TryReadActivateEncounterSkillPayload(payload, out skillEntityId);
    }
}
