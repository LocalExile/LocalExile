using System.Buffers.Binary;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Breach ("Brequel" / Keepers of the Flame) instance protocol messages (3.29.3c/d binary-verified):
/// Hiveblood sync (<c>S→C 0x02d2</c>), Genesis Tree state (<c>S→C 0x02cf</c>), Genesis Tree allocate/refund
/// (<c>C→S 0x02d0</c>/<c>0x02d1</c>), Wombgift fertilise (<c>C→S 0x02d3</c>), <c>BrequelStorage1</c> Ctrl+click
/// quick-move (<c>C→S 0x010d</c>), and <c>0x7b</c> UI kinds (<c>0x62</c> Genesis Tree, <c>0x64</c> Wombgift Locker).
/// </summary>
public static class InstanceBrequel
{
    /// <summary>S→C 0x7b kind <c>0x62</c> (98): Genesis Tree (<c>brequel_skill_tree_window</c>, HUD +0xa28, args ignored).</summary>
    public const byte GenesisTreeUi = 0x62;

    /// <summary>S→C 0x7b kind <c>0x64</c> (100): Wombgift Locker (<c>BrequelLocker</c>, HUD +0xd50, arg0 = locker entityId).</summary>
    public const byte WombgiftLockerUi = 0x64;

    /// <summary>Client UI gauge cap for Hiveblood (<c>0x1403bf997</c> / <c>0x1403ce4c3</c>: <c>fill = current * 1e-5f</c>).</summary>
    public const uint MaxHiveblood = 100_000;

    /// <summary>S→C 0x02cf: Genesis Tree passive state and per-subtree points (<c>0x1421b9220</c> → <c>0x141ef6400</c>).</summary>
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
    /// S→C 0x02cf: syncs Genesis Tree points for the 4 subtrees (<c>BrequelPassiveSubTrees</c> 0..3: Equipment, Currency,
    /// Uniques, Misc) and optionally the full allocated node set. When <paramref name="allocatedNodes"/> is null,
    /// <c>pointsOnly = 1</c> keeps the client's existing allocated nodes; otherwise <c>pointsOnly = 0</c>,
    /// <c>u16 BE count</c>, then <c>count × u16 LE</c> node IDs sorted ascending (required by client <c>std::set_difference</c>
    /// at <c>0x141ef6495</c>).
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
}
