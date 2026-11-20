using System.Numerics;

namespace GameServer.Core.Protocol.Instance;

/// <summary>One passive node mod stored inside an item's weapon passive tree (Mods bit 7, 0x1424bdb90 → 0x142436e50):
/// a Mods.dat HASH16 and one signed compact value per stat of that mod row.</summary>
public sealed record WeaponPassiveTreeNode(ushort ModHash, IReadOnlyList<int> Values)
{
    public IReadOnlyList<int> Values { get; init => field = Owned.List(value); } = Owned.List(Values);

    public bool Equals(WeaponPassiveTreeNode? other) =>
        other is not null && ModHash == other.ModHash && Values.SequenceEqual(other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ModHash);
        foreach (int v in Values) hash.Add(v);
        return hash.ToHashCode();
    }
}

/// <summary>
/// An item's weapon passive tree (Mods component flag bit 7, 0x0080; writer 0x1424bd880, reader 0x1424bdb90,
/// serialized after ObjectMagicProperties and before MemoryStrands/Intangibility).
/// <para>The 5-column triangular grid has <c>col + 1</c> rows per column (15 slots, bits 0..14):
/// <c>bitIndex(col, row) = col * (col + 1) / 2 + row</c>. Bit 15 (0x8000) of <see cref="Layout"/> MUST be 0 when
/// <see cref="HasTree"/> is true (the client sets <c>isRevealed</c> iff <c>(Layout &amp; 0x8000) == 0</c>).</para>
/// </summary>
public sealed record WeaponPassiveTreeState(
    uint BalanceItemLevel,
    uint Experience,
    bool HasTree,
    ushort Layout = 0,
    ushort Allocation = 0,
    ushort LeftParentConnections = 0,
    ushort RightParentConnections = 0,
    sbyte PendingCol0 = -1,
    sbyte PendingCol1 = -1,
    sbyte PendingCol2 = -1,
    sbyte PendingCol3 = -1,
    sbyte PendingCol4 = -1,
    IReadOnlyList<WeaponPassiveTreeNode>? Nodes = null)
{
    public const int ColumnCount = 5;
    public const int MaxSlots = 15;
    public const ushort ValidSlotMask = 0x7fff;

    public IReadOnlyList<WeaponPassiveTreeNode> Nodes { get; init => field = Owned.List(value ?? []); } = Owned.List(Nodes ?? []);

    /// <summary>Bit index (0..14) in the 15-slot triangular tree for column (0..4) and row (0..col).</summary>
    public static int BitIndex(int column, int row)
    {
        if (column is < 0 or >= ColumnCount) throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be 0..4.");
        if (row < 0 || row > column) throw new ArgumentOutOfRangeException(nameof(row), row, $"Row must be 0..{column} for column {column}.");
        return column * (column + 1) / 2 + row;
    }

    /// <summary>(Column, Row) for a slot bit index (0..14).</summary>
    public static (int Column, int Row) Coordinates(int bitIndex)
    {
        if (bitIndex is < 0 or >= MaxSlots) throw new ArgumentOutOfRangeException(nameof(bitIndex), bitIndex, "Bit index must be 0..14.");
        for (int col = 0; col < ColumnCount; col++)
        {
            int start = col * (col + 1) / 2;
            if (bitIndex <= start + col) return (col, bitIndex - start);
        }
        throw new ArgumentOutOfRangeException(nameof(bitIndex));
    }

    /// <summary>Whether the tree has a node at (column, row).</summary>
    public bool HasNode(int column, int row) => HasTree && (Layout & (1 << BitIndex(column, row))) != 0;

    /// <summary>Whether the node at (column, row) is present and allocated.</summary>
    public bool IsAllocated(int column, int row)
    {
        int mask = 1 << BitIndex(column, row);
        return HasTree && (Layout & mask) != 0 && (Allocation & mask) != 0;
    }

    /// <summary>Pending auto-allocation target row (-1 for none) for column 0..4.</summary>
    public sbyte PendingRow(int column) => column switch
    {
        0 => PendingCol0, 1 => PendingCol1, 2 => PendingCol2, 3 => PendingCol3, 4 => PendingCol4,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be 0..4."),
    };

    /// <summary>Returns a copy with the pending auto-allocation row for <paramref name="column"/> updated.</summary>
    public WeaponPassiveTreeState WithPendingRow(int column, sbyte row) => column switch
    {
        0 => this with { PendingCol0 = row },
        1 => this with { PendingCol1 = row },
        2 => this with { PendingCol2 = row },
        3 => this with { PendingCol3 = row },
        4 => this with { PendingCol4 = row },
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be 0..4."),
    };

    /// <summary>The node mod at (column, row), or null if absent.</summary>
    public WeaponPassiveTreeNode? NodeAt(int column, int row)
    {
        if (!HasTree) return null;
        int bit = BitIndex(column, row);
        if ((Layout & (1 << bit)) == 0) return null;
        int index = BitOperations.PopCount((uint)(Layout & ((1 << bit) - 1)));
        return index < Nodes.Count ? Nodes[index] : null;
    }

    /// <summary>Only the nodes present in <see cref="Layout"/> whose bit is set in <see cref="Allocation"/>, in bit order.</summary>
    public IEnumerable<WeaponPassiveTreeNode> AllocatedNodes()
    {
        if (!HasTree || Allocation == 0 || Nodes.Count == 0) yield break;
        int modIndex = 0;
        ushort layout = (ushort)(Layout & ValidSlotMask);
        for (int bit = 0; bit < MaxSlots; bit++)
        {
            if ((layout & (1 << bit)) == 0) continue;
            if (modIndex >= Nodes.Count) yield break;
            if ((Allocation & (1 << bit)) != 0) yield return Nodes[modIndex];
            modIndex++;
        }
    }

    /// <summary>Serializes the Mods bit-7 payload (little-endian, matching 0x1424bd880 / 0x1424bdb90).</summary>
    internal void Write(BlobWriter w)
    {
        w.U32(BalanceItemLevel).U32(Experience).U8((byte)(HasTree ? 1 : 0));
        if (!HasTree) return;
        if ((Layout & ~ValidSlotMask) != 0)
            throw new InvalidDataException($"WeaponPassiveTree Layout bit 15 must be 0 when HasTree is set (got 0x{Layout:x4}).");
        int expectedNodes = BitOperations.PopCount(Layout);
        if (Nodes.Count != expectedNodes)
            throw new InvalidDataException($"WeaponPassiveTree Layout 0x{Layout:x4} has {expectedNodes} bit(s), but {Nodes.Count} node mod(s) were supplied.");
        w.U16(Layout)
         .U16((ushort)(Allocation & Layout))
         .U16((ushort)(LeftParentConnections & ValidSlotMask))
         .U16((ushort)(RightParentConnections & ValidSlotMask))
         .U8(unchecked((byte)PendingCol0))
         .U8(unchecked((byte)PendingCol1))
         .U8(unchecked((byte)PendingCol2))
         .U8(unchecked((byte)PendingCol3))
         .U8(unchecked((byte)PendingCol4));
        foreach (var node in Nodes)
        {
            w.U16(node.ModHash);
            foreach (int value in node.Values) w.CompactI(value);
        }
    }

    public bool Equals(WeaponPassiveTreeState? other) =>
        other is not null
        && BalanceItemLevel == other.BalanceItemLevel
        && Experience == other.Experience
        && HasTree == other.HasTree
        && Layout == other.Layout
        && Allocation == other.Allocation
        && LeftParentConnections == other.LeftParentConnections
        && RightParentConnections == other.RightParentConnections
        && PendingCol0 == other.PendingCol0
        && PendingCol1 == other.PendingCol1
        && PendingCol2 == other.PendingCol2
        && PendingCol3 == other.PendingCol3
        && PendingCol4 == other.PendingCol4
        && Nodes.SequenceEqual(other.Nodes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BalanceItemLevel);
        hash.Add(Experience);
        hash.Add(HasTree);
        hash.Add(Layout);
        hash.Add(Allocation);
        hash.Add(LeftParentConnections);
        hash.Add(RightParentConnections);
        hash.Add(PendingCol0);
        hash.Add(PendingCol1);
        hash.Add(PendingCol2);
        hash.Add(PendingCol3);
        hash.Add(PendingCol4);
        foreach (var node in Nodes) hash.Add(node);
        return hash.ToHashCode();
    }
}
