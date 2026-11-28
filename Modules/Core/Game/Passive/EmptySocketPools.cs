using GameServer.Core.Protocol.Instance;

/// <summary>
/// The per-empty-socket modifiers of an equipped item (The Untouched Soul's four stats: Red -> flat Life,
/// Green -> flat Accuracy, Blue -> flat Mana, White -> All Elemental Resistances): each stat is multiplied by the
/// number of empty sockets of that colour on the active equipped items and joins its flat target bucket. Path of
/// Building counts the same way: CalcSetup.lua sums each slot's empty sockets per colour into the
/// <c>Empty*SocketsInAnySlot</c> multipliers and applies each modifier as a base contribution before increases.
/// </summary>
public static class EmptySocketPools
{
    /// <summary>Each counted socket colour, its per-empty-socket stat, and the flat stat it feeds.</summary>
    public static readonly (SocketColour Colour, uint Source, uint Target)[] PerEmptySocket =
    [
        (SocketColour.Red, Stat.MaximumLifePlusPerEmptyRedSocket, Stat.BaseMaximumLife),
        (SocketColour.Green, Stat.AccuracyRatingPlusPerEmptyGreenSocket, Stat.AccuracyRating),
        (SocketColour.Blue, Stat.MaximumManaPlusPerEmptyBlueSocket, Stat.BaseMaximumMana),
        (SocketColour.White, Stat.ElementalResistancePctPerEmptyWhiteSocket, Stat.BaseResistAllElementsPct),
    ];

    /// <summary>The empty sockets of <paramref name="colour"/> on these items: a socket with no gem in it.</summary>
    public static int Empty(IReadOnlyList<Item> items, SocketColour colour)
    {
        int count = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var sockets = items[i].Sockets;
            for (int s = 0; s < sockets.Count; s++)
            {
                var socket = sockets[s];
                if (socket.Colour == colour && socket.Gem is null) count++;
            }
        }
        return count;
    }

    /// <summary>Fold the per-empty-socket modifiers of <paramref name="active"/> into their flat target stats so pools,
    /// accuracy, resistances, and the character panel read them uniformly. Nothing changes when no active item carries
    /// one.</summary>
    public static void Apply(IReadOnlyList<Item> active, Dictionary<uint, int> sources)
    {
        for (int i = 0; i < PerEmptySocket.Length; i++)
        {
            var (colour, source, target) = PerEmptySocket[i];
            int per = sources.GetValueOrDefault(source);
            if (per == 0) continue;
            int count = Empty(active, colour);
            if (count == 0) continue;
            sources[target] = (int)Math.Clamp(sources.GetValueOrDefault(target) + (long)per * count,
                int.MinValue, int.MaxValue);
        }
    }
}
