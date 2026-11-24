using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static readonly (uint Source, uint Conversion, SocketColour Colour, bool GemColour)[] MinionSocketConversions =
    [
        (Stat.MinionBasePhysicalDamagePctToConvertToFirePerSocketedRedGem, Stat.MinionBasePhysicalDamagePctToConvertToFire, SocketColour.Red, true),
        (Stat.MinionBasePhysicalDamagePctToConvertToColdPerSocketedGreenGem, Stat.MinionBasePhysicalDamagePctToConvertToCold, SocketColour.Green, true),
        (Stat.MinionBasePhysicalDamagePctToConvertToLightningPerSocketedBlueGem, Stat.MinionBasePhysicalDamagePctToConvertToLightning, SocketColour.Blue, true),
        (Stat.MinionBasePhysicalDamagePctToConvertToChaosPerEmptyGemSocket, Stat.MinionBasePhysicalDamagePctToConvertToChaos, SocketColour.White, true),
        (Stat.MinionBasePhysicalDamagePctToConvertToFirePerRedSocketOnItem, Stat.MinionBasePhysicalDamagePctToConvertToFire, SocketColour.Red, false),
        (Stat.MinionBasePhysicalDamagePctToConvertToColdPerGreenSocketOnItem, Stat.MinionBasePhysicalDamagePctToConvertToCold, SocketColour.Green, false),
        (Stat.MinionBasePhysicalDamagePctToConvertToLightningPerBlueSocketOnItem, Stat.MinionBasePhysicalDamagePctToConvertToLightning, SocketColour.Blue, false),
        (Stat.MinionBasePhysicalDamagePctToConvertToChaosPerWhiteSocketOnItem, Stat.MinionBasePhysicalDamagePctToConvertToChaos, SocketColour.White, false),
    ];

    // The multiplier belongs to the item carrying the source modifier. Legacy items count socket colours. The
    // current rows count the gems' own colours and treat empty gem sockets as the separate chaos conversion.
    static void ApplyMinionSocketConversions(IReadOnlyList<Item> active, ItemCatalog catalog, Dictionary<uint, int> stats)
    {
        bool any = false;
        for (int i = 0; i < MinionSocketConversions.Length; i++)
            if (stats.ContainsKey(MinionSocketConversions[i].Source)) { any = true; break; }
        if (!any) return;
        for (int a = 0; a < active.Count; a++)
        {
            var item = active[a];
            if (item.Sockets.Count == 0) continue;
            var itemStats = PlainItemSum(item, catalog.Mods);
            for (int i = 0; i < MinionSocketConversions.Length; i++)
            {
                var (source, conversion, colour, gemColour) = MinionSocketConversions[i];
                if (!itemStats.TryGetValue(source, out int value) || value == 0) continue;
                int count = 0;
                for (int s = 0; s < item.Sockets.Count; s++)
                {
                    var socket = item.Sockets[s];
                    if (!gemColour ? socket.Colour == colour
                        : colour == SocketColour.White ? socket.Colour != SocketColour.Abyss && socket.Gem is null
                        : socket.Colour != SocketColour.Abyss && socket.Gem is { } gem
                            && catalog.ByHash(gem.TypeHash) is { } type && catalog.Gem(type)?.Colour == (int)colour)
                        count++;
                }
                if (count > 0)
                    GolemAdd(stats, conversion, ActorScale.Saturate((long)value * count));
            }
        }
    }
}
