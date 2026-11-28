namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The Mods component's flag bit 7 record (<c>0x1424bdb90</c>, the 0x1c8 bytes at <c>+0x140</c>): a Crucible weapon
/// tree. The client has no item component for it, so the whole tree travels inside the Mods component, right after
/// <see cref="MagicProperties"/> and before the trailing <see cref="Item.MemoryStrands"/>/<see cref="Item.Intangibility"/>
/// bytes.
/// <para>
/// The reader takes a <c>u32</c> row of WeaponPassiveTreeBalancePerItemLevel (the five Bar1..Bar5 unlock thresholds at
/// <c>+0x150</c>), a <c>u32</c> progress value compared against those bars (tier <c>i</c> unlocks when
/// <c>Progress &gt;= Bar(i+1)</c>, and a locked tier deallocates whatever it held), a <c>u8</c> flag that says whether
/// a tree follows, then four <c>u16</c> bitsets, five <c>u8</c> tier picks and finally one mod per node.
/// </para>
/// <para>
/// The node count is <b>not</b> sent: the client counts the set bits of <see cref="NodeMask"/> (a popcount table at
/// <c>0x1431f5d10</c>) and reads exactly that many mods, in slot order. Nodes are at most 16, spread over five tiers, so
/// <see cref="Nodes"/> must have as many entries as <see cref="NodeMask"/> has bits set.
/// </para>
/// </summary>
public sealed record WeaponTree
{
    /// <summary>The WeaponPassiveTreeBalancePerItemLevel row, i.e. the item level clamped to 1..100.</summary>
    public uint BalanceRow { get; init; } = 1;

    /// <summary>Tree progress measured in the row's Bar units. Send a value at or above Bar5 to unlock every tier.</summary>
    public uint Progress { get; init; }

    /// <summary>Which of the 16 slots carry a node mod. Its popcount is how many mods the client reads.</summary>
    public ushort NodeMask { get; init; }

    /// <summary>Which slots the player has allocated (<c>0x1424bcea0</c>).</summary>
    public ushort AllocatedMask { get; init; }

    /// <summary>Left-parent links, drawn as the tree's connections (<c>0x1424bd5a0</c>).</summary>
    public ushort LeftMask { get; init; }

    /// <summary>Right-parent links, drawn as the tree's connections (<c>0x1424bd5a0</c>).</summary>
    public ushort RightMask { get; init; }

    /// <summary>One node pick per tier, in tier order; <c>0xff</c> leaves the tier unallocated.</summary>
    public IReadOnlyList<byte> TierPicks { get; init => field = Owned.List(value); } = [0xff, 0xff, 0xff, 0xff, 0xff];

    /// <summary>The node mods, one per set bit of <see cref="NodeMask"/>, in slot order. Each is a Mods.dat row
    /// (GenerationType 31) whose first stat is <c>local_weapon_passive_tree_granted_passive_hash</c>, the
    /// WeaponPassiveSkills.dat row that gives the node its tier, position and icon.</summary>
    public IReadOnlyList<ItemMod> Nodes { get; init => field = Owned.List(value); } = [];

    IReadOnlyList<ItemMod>? _allocated;
    int _allocatedKey;

    /// <summary>The allocated nodes as ordinary hidden mods, one per set bit of <see cref="AllocatedMask"/> that also
    /// carries a node. The tree record only draws the tree; the effects come from the item's mod lists, so a tree that
    /// should do something puts these in the hidden list. Kept once per allocation: a stat build asks every item for
    /// them, and a <c>with</c> that changes the masks rebuilds them (the copy carries the old cache).</summary>
    public IReadOnlyList<ItemMod> AllocatedMods
    {
        get
        {
            int key = HashCode.Combine(NodeMask, AllocatedMask, Nodes.Count);
            if (_allocated is null || _allocatedKey != key) (_allocated, _allocatedKey) = (BuildAllocated(), key);
            return _allocated;
        }
    }

    IReadOnlyList<ItemMod> BuildAllocated()
    {
        if (Nodes.Count == 0) return [];
        List<ItemMod> mods = [];
        int index = 0;
        for (int slot = 0; slot < 16; slot++)
        {
            if ((NodeMask & (1 << slot)) == 0) continue;
            if ((AllocatedMask & (1 << slot)) != 0 && index < Nodes.Count)
                mods.Add(new ItemMod(ModSection.Hidden, Nodes[index].Hash, Nodes[index].Values));
            index++;
        }
        return mods;
    }

    public void Write(BlobWriter w)
    {
        w.U32(BalanceRow).U32(Progress).U8(1);
        w.U16(NodeMask).U16(AllocatedMask).U16(LeftMask).U16(RightMask);
        for (int i = 0; i < 5; i++) w.U8(i < TierPicks.Count ? TierPicks[i] : (byte)0xff);
        foreach (ItemMod node in Nodes)
        {
            w.U16(node.Hash);
            foreach (int value in node.Values) w.CompactI(value);
        }
    }
}
