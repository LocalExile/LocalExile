using System.Buffers.Binary;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Breach ("Brequel" / Keepers of the Flame) instance messages the framework registers: the <c>0x7b</c> UI kinds
/// (<c>0x62</c> Genesis Tree, <c>0x64</c> Wombgift Locker) and the fixed C→S lengths of Genesis Tree allocate/refund
/// (<c>0x02d0</c>/<c>0x02d1</c>), Wombgift fertilise (<c>0x02d3</c>) and the <c>BrequelStorage1</c> Ctrl+click quick-move
/// (<c>0x010d</c>). The Breach module builds and reads the message bodies (BreachGenesisTree, BreachHiveblood,
/// BreachRulesets).
/// </summary>
public static class InstanceBrequel
{
    /// <summary>S→C 0x7b kind <c>0x62</c> (98): Genesis Tree (<c>brequel_skill_tree_window</c>, HUD +0xa28, args ignored).</summary>
    public const byte GenesisTreeUi = 0x62;

    /// <summary>S→C 0x7b kind <c>0x64</c> (100): Wombgift Locker (<c>BrequelLocker</c>, HUD +0xd50, arg0 = locker entityId).</summary>
    public const byte WombgiftLockerUi = 0x64;

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

    /// <summary>Reads C→S 0x010d (8 bytes: <c>BrequelStorage1</c> Ctrl+click quick-move).</summary>
    public static InstanceItems.LeagueWindowMove ReadLockerMove(ReadOnlySpan<byte> m) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(m[2..]), m[6] != 0, m[7] != 0);
}
