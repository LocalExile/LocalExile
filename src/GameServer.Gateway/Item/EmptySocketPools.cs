using GameServer.Core.Protocol.Instance;

/// <summary>
/// The per-empty-socket pool modifiers of an equipped item ("+40 to maximum Life for each Empty Red Socket on any
/// Equipped Item", the Gold Amulet's <c>IncreasedLifeEmptyRedSocketUnique__1</c>, and its mana counterpart): the
/// character's stat is multiplied by the number of empty sockets of that colour on all the active equipped items and
/// joins the ordinary flat pool bucket. Path of Building counts the same way: CalcSetup.lua1351-1353 sums each slot's
/// empty sockets per colour into the <c>EmptyRedSocketsInAnySlot</c> / <c>EmptyBlueSocketsInAnySlot</c> multipliers, and
/// ModCache.lua2043 is the Life mod — <c>Life BASE 40</c> with <c>Multiplier EmptyRedSocketsInAnySlot</c>, i.e. flat
/// life before the increases, which is what <c>base_maximum_life</c> is here.
/// </summary>
public static class EmptySocketPools
{
    /// <summary>The character stat for each socket colour that is counted, and the flat pool stat it adds to.</summary>
    public static readonly IReadOnlyList<(SocketColour Colour, string Stat, string Pool)> PerEmptySocket =
    [
        (SocketColour.Red, "maximum_life_+_per_empty_red_socket", "base_maximum_life"),
        (SocketColour.Blue, "maximum_mana_+_per_empty_blue_socket", "base_maximum_mana"),
    ];

    /// <summary>The empty sockets of <paramref name="colour"/> on these items: a socket with no gem in it.</summary>
    public static int Empty(IEnumerable<Item> items, SocketColour colour) =>
        items.Sum(i => i.Sockets.Count(s => s.Colour == colour && s.Gem is null));

    /// <summary>Fold the per-empty-socket modifiers of <paramref name="active"/> into their flat pool stats, so the
    /// pools and the character panel read them where <c>base_maximum_life</c> and <c>base_maximum_mana</c> are read.
    /// Nothing changes when no active item carries one.</summary>
    public static void Apply(IReadOnlyList<Item> active, StatCatalog stats, Dictionary<uint, int> sources)
    {
        foreach (var (colour, stat, pool) in PerEmptySocket)
        {
            if (stats.Key(stat) is not uint key || stats.Key(pool) is not uint poolKey) continue;
            int per = sources.GetValueOrDefault(key);
            int count = per == 0 ? 0 : Empty(active, colour);
            if (count == 0) continue;
            sources[poolKey] = (int)Math.Clamp(sources.GetValueOrDefault(poolKey) + (long)per * count,
                int.MinValue, int.MaxValue);
        }
    }
}
