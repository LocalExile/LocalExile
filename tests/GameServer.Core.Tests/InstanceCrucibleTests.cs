using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class InstanceCrucibleTests
{
    [Fact]
    public void Mods_bit_7_unrevealed_and_revealed_weapon_passive_tree_wire_layout()
    {
        // Unrevealed tree (HasTree = false): 9 bytes (u32 LE BalanceItemLevel, u32 LE Experience, u8 0)
        // placed immediately after ObjectMagicProperties and before MemoryStrands / Intangibility.
        var unrevealed = new Item(1, ["Mods"], 0)
        {
            ItemLevel = 68,
            MemoryStrands = 0x11,
            Intangibility = 0x22,
            WeaponPassiveTree = new WeaponPassiveTreeState(BalanceItemLevel: 68, Experience: 1234, HasTree: false),
        };
        Assert.Equal(
            "01000000" + "0000" +       // type hash (LE), item flags
            "44" + "00" + "8000" +      // Mods: ilvl=68, reqLvl=0, flags=0x0080 (bit 7)
            "0800" + "00" +             // ObjectMagicProperties: normal | identified, u7a=0
            "44000000" +                // BalanceItemLevel = 68 (u32 LE)
            "D2040000" +                // Experience = 1234 (u32 LE)
            "00" +                      // HasTree = 0
            "11" + "22",                // MemoryStrands, Intangibility
            Convert.ToHexString(unrevealed.Payload()));

        // Revealed tree (HasTree = true): 4 x u16 LE bitsets, 5 x sbyte pending rows (default 0xFF),
        // then popcount(Layout) standard mods in ascending bit-index order (0..14).
        int bit0 = WeaponPassiveTreeState.BitIndex(0, 0); // 0
        int bit1 = WeaponPassiveTreeState.BitIndex(1, 0); // 1
        int bit4 = WeaponPassiveTreeState.BitIndex(2, 1); // 4
        Assert.Equal((0, 0), WeaponPassiveTreeState.Coordinates(bit0));
        Assert.Equal((1, 0), WeaponPassiveTreeState.Coordinates(bit1));
        Assert.Equal((2, 1), WeaponPassiveTreeState.Coordinates(bit4));

        ushort layout = (ushort)((1 << bit0) | (1 << bit1) | (1 << bit4));     // 0x0013
        ushort allocation = (ushort)((1 << bit0) | (1 << bit1));               // 0x0003
        ushort leftParents = (ushort)(1 << bit4);                              // 0x0010
        ushort rightParents = (ushort)((1 << bit1) | (1 << bit4));             // 0x0012
        var n0 = new WeaponPassiveTreeNode(0x1234, [5, -3]);
        var n1 = new WeaponPassiveTreeNode(0x5678, [10]);
        var n4 = new WeaponPassiveTreeNode(0x9abc, []);
        var tree = new WeaponPassiveTreeState(
            BalanceItemLevel: 84, Experience: 50000, HasTree: true,
            Layout: layout, Allocation: allocation,
            LeftParentConnections: leftParents, RightParentConnections: rightParents,
            PendingCol2: 1,
            Nodes: [n0, n1, n4]);

        var revealed = new Item(1, ["Mods"], 0)
        {
            ItemLevel = 84,
            MemoryStrands = 0x33,
            Intangibility = 0x44,
            WeaponPassiveTree = tree,
        };
        Assert.Equal(
            "01000000" + "0000" +
            "54" + "00" + "8000" +      // Mods: ilvl=84, reqLvl=0, flags=0x0080
            "0800" + "00" +             // ObjectMagicProperties
            "54000000" +                // BalanceItemLevel = 84
            "50C30000" +                // Experience = 50000
            "01" +                      // HasTree = 1
            "1300" +                    // Layout = 0x0013
            "0300" +                    // Allocation = 0x0003
            "1000" +                    // LeftParentConnections = 0x0010
            "1200" +                    // RightParentConnections = 0x0012
            "FFFF01FFFF" +              // PendingCol0..4 = [-1, -1, 1, -1, -1]
            "3412" + "05" + "7D" +      // Node bit 0: hash 0x1234, CompactI(5)=0x05, CompactI(-3)=0x7D
            "7856" + "0A" +             // Node bit 1: hash 0x5678, CompactI(10)=0x0A
            "BC9A" +                    // Node bit 4: hash 0x9ABC, 0 stats
            "33" + "44",                // MemoryStrands, Intangibility
            Convert.ToHexString(revealed.Payload()));

        // Helper & equality checks.
        Assert.Equal(n0, tree.NodeAt(0, 0));
        Assert.Equal(n4, tree.NodeAt(2, 1));
        Assert.Null(tree.NodeAt(1, 1));
        Assert.Equal([n0, n1], tree.AllocatedNodes().ToArray());
        Assert.Equal(tree, tree with { Nodes = [new(0x1234, [5, -3]), new(0x5678, [10]), new(0x9abc, [])] });

        // Bit 15 guard and popcount mismatch guard.
        var badBit15 = new Item(1, ["Mods"], 0)
        {
            WeaponPassiveTree = new(1, 0, true, Layout: 0x8001, Nodes: [n0, n1]),
        };
        Assert.Throws<InvalidDataException>(() => badBit15.Payload());

        var badCount = new Item(1, ["Mods"], 0)
        {
            WeaponPassiveTree = new(1, 0, true, Layout: 0x0003, Nodes: [n0]),
        };
        Assert.Throws<InvalidDataException>(() => badCount.Payload());
    }

    [Fact]
    public void Crucible_client_and_server_messages_frame_and_encode_big_endian()
    {
        // C→S 0x0116 (26 bytes BE): Orb of Scouring node unallocation.
        byte[] unalloc = Convert.FromHexString(
            "0116" +
            "00000001" + "0000002A" +
            "00000002" + "00000007" +
            "00000003" + "00000002");
        Assert.Equal(InstanceCrucible.WeaponPassiveUnallocateLength, unalloc.Length);
        Assert.Equal(26, InstanceProtocol.KnownClientLength(InstanceCrucible.WeaponPassiveUnallocateId));
        Assert.Equal(new InstanceCrucible.WeaponPassiveUnallocate(1, 0x2A, 2, 7, 3, 2),
            InstanceCrucible.ReadWeaponPassiveUnallocate(unalloc));

        // C→S 0x0299 (18 bytes BE): combine request.
        byte[] combineReq = Convert.FromHexString("0299" + "00000001" + "00000010" + "00000001" + "00000020");
        Assert.Equal(InstanceCrucible.CombineRequestLength, combineReq.Length);
        Assert.Equal(18, InstanceProtocol.KnownClientLength(InstanceCrucible.CombineRequestId));
        Assert.Equal(new InstanceCrucible.CombineRequest(1, 0x10, 1, 0x20),
            InstanceCrucible.ReadCombineRequest(combineReq));

        // C→S 0x029b (10 bytes BE): Antique/Scouring furnace request.
        byte[] uniqueReq = Convert.FromHexString("029B" + "00000001" + "00000033");
        Assert.Equal(InstanceCrucible.UniqueForgeRequestLength, uniqueReq.Length);
        Assert.Equal(10, InstanceProtocol.KnownClientLength(InstanceCrucible.UniqueForgeRequestId));
        Assert.Equal(new InstanceCrucible.UniqueForgeRequest(1, 0x33),
            InstanceCrucible.ReadUniqueForgeRequest(uniqueReq));

        // Framing of all three C→S opcodes back-to-back.
        var split = InstanceProtocol.SplitClientMessages([.. unalloc, .. combineReq, .. uniqueReq], out var partial, out var unknown);
        Assert.Equal([26, 18, 10], split.Select(m => m.Bytes.Length).ToArray());
        Assert.Empty(partial);
        Assert.Empty(unknown);

        // S→C 0x007b kind 0x4a (combining) and 0x4b (unique/scouring furnace with arg1 & arg2).
        Assert.Equal(
            "007B4A00000001000000000000000000000000",
            Convert.ToHexString(InstanceCrucible.OpenCombiningUi(uber: true)));
        Assert.Equal(
            "007B4B00000002000012340000000000000000",
            Convert.ToHexString(InstanceCrucible.OpenUniqueForgeUi(InstanceCrucible.ScouringForgeMode, 0x1234)));

        // S→C 0x029a (combine result with CompactUInt mutation count and 3-bit flags) and 0x029c.
        byte[] combineRes = InstanceCrucible.CombineResult(1, 0x20,
        [
            new(0, 0, InstanceCrucible.NodeMutationFlags.UpgradedTier),
            new(2, 1, InstanceCrucible.NodeMutationFlags.DowngradedTier | InstanceCrucible.NodeMutationFlags.Mutated),
        ]);
        Assert.Equal(
            "029A" + "00000001" + "00000020" + "02" +
            "00000000" + "00000000" + "01" +
            "00000002" + "00000001" + "06",
            Convert.ToHexString(combineRes));

        Assert.Equal(
            "029C" + "00000001" + "00000033",
            Convert.ToHexString(InstanceCrucible.UniqueForgeResult(1, 0x33)));

        // QuestObject accepts ControlZone (CrucibleDevice) and CrucibleCombiningBench component chains.
        string[] crucibleDevice = ["Positioned", "BaseEvents", "InteractionAction", "Animated", "TriggerableBlockage", "MinimapIcon", "StateMachine", "ControlZone"];
        string[] combiningBench = ["Positioned", "BaseEvents", "Animated", "InteractionAction", "Transitionable", "MinimapIcon"];
        Assert.True(QuestObject.CanSerialize(crucibleDevice, haveStateMachine: true));
        Assert.True(QuestObject.CanSerialize(combiningBench));
        var withZone = new QuestObject(10, 20, crucibleDevice) { StateMachine = new([0, 0, 0], 0) }.Blob();
        var withoutZone = new QuestObject(10, 20, crucibleDevice[..^1]) { StateMachine = new([0, 0, 0], 0) }.Blob();
        Assert.Equal(withoutZone, withZone);
    }
}
