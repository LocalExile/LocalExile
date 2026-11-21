using System.Numerics;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Pure 15-slot triangular DAG math for Crucible Weapon Passive Trees matching client <c>0x1424bc940</c>,
/// <c>0x1424bd5a0</c>, <c>0x1424bd880</c>, and <c>0x1424bcea0</c>:
/// 5 columns (<c>col = 0..4</c>), <c>col + 1</c> rows per column (<c>row = 0..col</c>),
/// <c>bitIndex(col, row) = col * (col + 1) / 2 + row</c> (bits <c>0..14</c>; bit 15 must be 0 when revealed).
/// </summary>
public static class CrucibleTreeTopology
{
    public const int Columns = 5;
    public const int MaxSlots = 15;
    public const ushort ValidLayoutMask = 0x7FFF;

    public readonly record struct LayoutMasks(ushort Layout, ushort LeftParents, ushort RightParents);

    // Our own numbers, GGG never published them. Known bounds only: 2 to 5 columns (poewiki), and the last depths
    // are rare and on endgame items (GGG's Crucible video, 2:02).
    const int DepthMidItemLevel = 30, DepthEndgameItemLevel = 68;
    // Percent chance of 2, 3 and 4 columns; the rest is 5.
    static readonly int[] DepthChanceLow = [40, 45, 15], DepthChanceMid = [15, 45, 40], DepthChanceEndgame = [5, 30, 40];
    // Percent chance of 1, 2 and 3 nodes in columns 1..4.
    static readonly int[][] NodesPerColumnChance = [[15, 85, 0], [7, 38, 55], [20, 56, 24], [60, 36, 4]];

    /// <summary>Index of the weight a percent roll lands in; the count of weights when it lands past them all.</summary>
    static int Pick(int[] chances, Random random)
    {
        int roll = random.Next(100), i = 0;
        while (i < chances.Length && (roll -= chances[i]) >= 0) i++;
        return i;
    }

    public static int BitIndex(int col, int row) => col * (col + 1) / 2 + row;

    public static bool ValidCoords(int col, int row) => (uint)col < Columns && (uint)row <= (uint)col;

    public static (int Col, int Row) Coords(int bit)
    {
        for (int col = 0; col < Columns; col++)
        {
            int start = col * (col + 1) / 2;
            if (bit < start + col + 1) return (col, bit - start);
        }
        throw new ArgumentOutOfRangeException(nameof(bit));
    }

    public static ushort BitMask(int col, int row) => (ushort)(1 << BitIndex(col, row));

    public static bool HasBit(ushort mask, int col, int row) => ValidCoords(col, row) && ((mask >> BitIndex(col, row)) & 1) != 0;

    /// <summary>Returns the bit indices present in <paramref name="layout"/> in strictly ascending order (0..14).</summary>
    public static List<int> BitsInOrder(ushort layout)
    {
        ushort clean = (ushort)(layout & ValidLayoutMask);
        var list = new List<int>(BitOperations.PopCount(clean));
        for (int b = 0; b < MaxSlots; b++)
            if (((clean >> b) & 1) != 0) list.Add(b);
        return list;
    }

    /// <summary>Unlocked column depth (0..5) for <paramref name="experience"/> against <paramref name="balance"/>.</summary>
    public static int UnlockedDepth(uint experience, CrucibleTables.LevelBalance balance)
    {
        int exp = (int)Math.Min(experience, int.MaxValue);
        int depth = 0;
        for (int col = 1; col <= Columns; col++)
        {
            if (exp >= balance.Bar(col)) depth = col;
            else break;
        }
        return depth;
    }

    /// <summary>True if <paramref name="parentCol"/>, <paramref name="parentRow"/> is a connected parent of <paramref name="col"/>, <paramref name="row"/>.</summary>
    public static bool IsConnectedToParent(ushort layout, ushort leftParents, ushort rightParents, int col, int row, int parentRow)
    {
        if (col <= 0 || !HasBit(layout, col, row) || !HasBit(layout, col - 1, parentRow)) return false;
        int bit = BitIndex(col, row);
        // Left parent = (col - 1, row - 1); Right parent = (col - 1, row).
        // Per 0x1424bd5a0, edge nodes (row == 0 or row == col) are always connected to their sole triangle parent.
        if (parentRow == row - 1)
            return row == col || ((leftParents >> bit) & 1) != 0;
        if (parentRow == row)
            return row == 0 || ((rightParents >> bit) & 1) != 0;
        return false;
    }

    /// <summary>Returns the allocated row (0..col) in <paramref name="col"/>, or -1 if none is allocated.</summary>
    public static int AllocatedRowInColumn(ushort allocation, int col)
    {
        if ((uint)col >= Columns) return -1;
        for (int row = 0; row <= col; row++)
            if (HasBit(allocation, col, row)) return row;
        return -1;
    }

    /// <summary>Highest column index (0..4) with an allocated node, or -1 if nothing is allocated.</summary>
    public static int DeepestAllocatedColumn(ushort allocation)
    {
        for (int col = Columns - 1; col >= 0; col--)
            if (AllocatedRowInColumn(allocation, col) >= 0) return col;
        return -1;
    }

    /// <summary>Returns whether <c>(col, row)</c> can currently be allocated (the checks of <c>0x1424bcea0</c>).
    /// The client replaces the node of a column; refusing a column that has one is our rule: an allocated skill
    /// changes only through an Orb of Scouring (GGG's Crucible Q&amp;A).</summary>
    public static bool CanAllocate(
        ushort layout, ushort allocation, ushort leftParents, ushort rightParents,
        uint experience, CrucibleTables.LevelBalance balance, int col, int row)
    {
        if (!ValidCoords(col, row) || !HasBit(layout, col, row)) return false;
        if (col >= UnlockedDepth(experience, balance)) return false;
        if (AllocatedRowInColumn(allocation, col) >= 0) return false;
        if (col == 0) return true;
        int parentRow = AllocatedRowInColumn(allocation, col - 1);
        return parentRow >= 0 && IsConnectedToParent(layout, leftParents, rightParents, col, row, parentRow);
    }

    /// <summary>Returns whether <c>(col, row)</c> is the deepest allocated node (required for Orb of Scouring unallocation).</summary>
    public static bool CanUnallocate(ushort allocation, int col, int row)
    {
        if (!ValidCoords(col, row) || !HasBit(allocation, col, row)) return false;
        return DeepestAllocatedColumn(allocation) == col;
    }

    /// <summary>Returns all present child rows in <c>col + 1</c> connected to <c>(col, row)</c>.</summary>
    public static List<int> ConnectedChildren(ushort layout, ushort leftParents, ushort rightParents, int col, int row)
    {
        var result = new List<int>(2);
        if (col < 0 || col >= Columns - 1) return result;
        if (IsConnectedToParent(layout, leftParents, rightParents, col + 1, row, row)) result.Add(row);
        if (IsConnectedToParent(layout, leftParents, rightParents, col + 1, row + 1, row)) result.Add(row + 1);
        return result;
    }

    /// <summary>
    /// Generates a valid connected 5-column triangular Crucible tree layout with randomized branching.
    /// Guarantees bit 15 is 0, root (0,0) is present, every node in col &gt; 0 has at least one connected parent in
    /// col - 1, and edge parent bits match <c>0x1424bd880</c>.
    /// </summary>
    public static LayoutMasks GenerateLayout(int itemLevel, Random random)
    {
        // Column 0 always has root (0, 0).
        ushort layout = 1;
        ushort left = 0;
        ushort right = 0;

        int maxCol = 1 + Pick(itemLevel >= DepthEndgameItemLevel ? DepthChanceEndgame
            : itemLevel >= DepthMidItemLevel ? DepthChanceMid : DepthChanceLow, random);

        for (int col = 1; col <= maxCol; col++)
        {
            // Determine candidate rows in this column that have at least one existing parent in col - 1.
            var candidates = new List<(int Row, bool HasLeftParent, bool HasRightParent)>();
            for (int row = 0; row <= col; row++)
            {
                bool hasLeft = row > 0 && HasBit(layout, col - 1, row - 1);
                bool hasRight = row < col && HasBit(layout, col - 1, row);
                // Edge nodes can ONLY connect to their sole triangle parent; interior nodes need at least one.
                if (row == 0 && !hasRight) continue;
                if (row == col && !hasLeft) continue;
                if (!hasLeft && !hasRight) continue;
                candidates.Add((row, hasLeft, hasRight));
            }
            if (candidates.Count == 0) break;

            int count = Math.Clamp(1 + Pick(NodesPerColumnChance[col - 1], random), 1, candidates.Count);

            // Shuffle candidates and pick `count`.
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            var picked = candidates.Take(count).OrderBy(c => c.Row).ToList();

            foreach (var (row, hasLeft, hasRight) in picked)
            {
                int bit = BitIndex(col, row);
                layout |= (ushort)(1 << bit);
                if (row == 0)
                {
                    right |= (ushort)(1 << bit);
                }
                else if (row == col)
                {
                    left |= (ushort)(1 << bit);
                }
                else
                {
                    if (hasLeft && hasRight)
                    {
                        // Our own number for a fresh tree: players counted a third each (one link, the other, both) on combined trees.
                        int edgeRoll = random.Next(3);
                        if (edgeRoll != 1) left |= (ushort)(1 << bit);
                        if (edgeRoll != 0) right |= (ushort)(1 << bit);
                    }
                    else if (hasLeft) left |= (ushort)(1 << bit);
                    else if (hasRight) right |= (ushort)(1 << bit);
                }
            }
        }

        return Normalize(layout, left, right);
    }

    /// <summary>
    /// Cleans a layout + connection mask pair so every present node is reachable from (0, 0), bit 15 is 0,
    /// and edge-node parent bits conform to <c>0x1424bd880</c>.
    /// </summary>
    public static LayoutMasks Normalize(ushort layout, ushort left, ushort right)
    {
        layout &= ValidLayoutMask;
        if ((layout & 1) == 0) return new(0, 0, 0);

        ushort cleanLayout = 1;
        ushort cleanLeft = 0;
        ushort cleanRight = 0;

        for (int col = 1; col < Columns; col++)
        {
            for (int row = 0; row <= col; row++)
            {
                int bit = BitIndex(col, row);
                if (((layout >> bit) & 1) == 0) continue;

                bool hasLeftParent = row > 0 && HasBit(cleanLayout, col - 1, row - 1);
                bool hasRightParent = row < col && HasBit(cleanLayout, col - 1, row);

                bool linkLeft = false, linkRight = false;
                if (row == 0)
                {
                    if (!hasRightParent) continue;
                    linkRight = true;
                }
                else if (row == col)
                {
                    if (!hasLeftParent) continue;
                    linkLeft = true;
                }
                else
                {
                    linkLeft = hasLeftParent && ((left >> bit) & 1) != 0;
                    linkRight = hasRightParent && ((right >> bit) & 1) != 0;
                    if (!linkLeft && !linkRight)
                    {
                        // Repair broken interior link if either triangle parent exists.
                        if (hasLeftParent) linkLeft = true;
                        else if (hasRightParent) linkRight = true;
                        else continue;
                    }
                }

                cleanLayout |= (ushort)(1 << bit);
                if (linkLeft) cleanLeft |= (ushort)(1 << bit);
                if (linkRight) cleanRight |= (ushort)(1 << bit);
            }
        }

        return new(cleanLayout, cleanLeft, cleanRight);
    }
}
