namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The base magic properties both items (their Mods component) and monsters (their ObjectMagicProperties component)
/// carry, read by 0x142201d70: u16 flags (bits 0-2 rarity, bit 3 identified, one bit per mod list, bit 7 a custom name,
/// bit 8 Words.dat name parts), u8 (+0x7a), then the lists in the reader's order: bit 4 (implicit), 6 (enchant),
/// 10 (scourge), 11 (hidden), and only when identified the name (bit 7: u32 length + UTF-16; else bit 8: u8 n × u32
/// Words.dat HASH32) and bit 5 (explicit). A list is u8 count + mods; a mod (0x142430380) is its u16 Mods.dat HASH16
/// and a signed compact value per stat of the row (Agents/docs/protocol/13-items.md §2a).
/// </summary>
public static class MagicProperties
{
    public const ushort Identified = 0x08, CustomName = 0x80, NameWords = 0x100;

    static readonly ModSection[] Order = [ModSection.Implicit, ModSection.Enchant, ModSection.Scourge, ModSection.Hidden];

    /// <summary>Writes the base magic properties. Unidentified objects send no explicit list and no name;
    /// <paramref name="customName"/> wins over <paramref name="nameWords"/> (the reader takes bit 7 first).</summary>
    public static void Write(BlobWriter w, byte rarity, bool identified, IReadOnlyList<ItemMod> mods,
        IReadOnlyList<uint>? nameWords = null, string? customName = null, byte u7a = 0)
    {
        var sections = mods.Where(m => identified || m.Section != ModSection.Explicit).GroupBy(m => m.Section)
            .ToDictionary(g => g.Key, g => g.ToList());
        bool custom = identified && customName is not null;
        bool words = identified && !custom && nameWords is { Count: > 0 };
        ushort flags = (ushort)(rarity & 7 | (identified ? Identified : 0) | (custom ? CustomName : 0) | (words ? NameWords : 0));
        foreach (var s in sections.Keys) flags |= (ushort)(1 << (byte)s);
        w.U16(flags).U8(u7a);
        foreach (var s in Order) List(w, sections, s);
        // The name comes between the lists read for every object and the identified-only explicit list.
        if (custom) w.WStr32(customName!);
        else if (words)
        {
            w.U8((byte)Math.Min(nameWords!.Count, 255));
            foreach (uint word in nameWords.Take(255)) w.U32(word);
        }
        List(w, sections, ModSection.Explicit);
    }

    static void List(BlobWriter w, Dictionary<ModSection, List<ItemMod>> sections, ModSection s)
    {
        if (!sections.TryGetValue(s, out var mods)) return;
        w.U8((byte)Math.Min(mods.Count, 255));
        foreach (var m in mods.Take(255))
        {
            w.U16(m.Hash);
            foreach (int v in m.Values) w.CompactI(v);
        }
    }
}
