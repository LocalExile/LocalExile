namespace GameServer.Core.Protocol.Instance;

/// <summary>IncursionTemple item component 158 (slot 5 0x1424aeb80): u32 level, eleven u16 room keys,
/// then four connection bytes. The module supplies the room plan; the protocol only retains and writes it.</summary>
public sealed record IncursionTempleData(uint AreaLevel, IReadOnlyList<ushort> Rooms, uint Connections)
{
    public IReadOnlyList<ushort> Rooms { get; init => field = Owned.List(value); } = Owned.List(Rooms);

    public void Write(BlobWriter writer)
    {
        writer.U32(AreaLevel);
        for (int i = 0; i < 11; i++) writer.U16(i < Rooms.Count ? Rooms[i] : (ushort)0);
        writer.U32(Connections);
    }
}

/// <summary>One ExpeditionSaga location: WorldAreas HASH16, faction in bits 0..1, retained variant in bits 2..4,
/// implicit count in bits 5..7, then ordinary Mods HASH16/compact-value entries (0x1424bf610).</summary>
public sealed record ExpeditionSagaArea(ushort AreaHash, byte Faction, byte Variant, IReadOnlyList<ItemMod> Implicits)
{
    public IReadOnlyList<ItemMod> Implicits { get; init => field = Owned.List(value); } = Owned.List(Implicits);
}

/// <summary>ExpeditionSaga item component 160: area level, location count, and each location's complete implicit rolls.</summary>
public sealed record ExpeditionSagaData(byte AreaLevel, IReadOnlyList<ExpeditionSagaArea> Areas)
{
    public IReadOnlyList<ExpeditionSagaArea> Areas { get; init => field = Owned.List(value); } = Owned.List(Areas);

    public void Write(BlobWriter writer)
    {
        int count = Math.Min(Areas.Count, byte.MaxValue);
        writer.U8(AreaLevel).U8((byte)count);
        foreach (var area in Areas.Take(count))
        {
            int mods = Math.Min(area.Implicits.Count, 7);
            writer.U16(area.AreaHash).U8((byte)((area.Faction & 3) | ((area.Variant & 7) << 2) | (mods << 5)));
            foreach (var mod in area.Implicits.Take(mods))
            {
                writer.U16(mod.Hash);
                foreach (int value in mod.Values) writer.CompactI(value);
            }
        }
    }
}

/// <summary>MercenaryContract item component 173 (slot 5 0x1424ac0f0, write 0x1424ac0a0): u8 flag (+0x78) followed by
/// <see cref="InstanceMercenaries.MercenaryBaseRecord"/> (+0x10) for Mercenary Warrant rematch items.</summary>
public sealed record MercenaryContractData(InstanceMercenaries.MercenaryBaseRecord Mercenary, byte Flag = 0)
{
    public void Write(BlobWriter writer)
    {
        writer.U8(Flag);
        InstanceMercenaries.WriteBaseRecord(writer, Mercenary);
    }
}
