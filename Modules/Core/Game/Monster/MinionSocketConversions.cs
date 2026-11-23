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
        if (!MinionSocketConversions.Any(rule => stats.ContainsKey(rule.Source))) return;
        foreach (var item in active)
        {
            if (item.Sockets.Count == 0) continue;
            foreach (var (key, value) in ItemModStats.Values(item, catalog.Mods))
            {
                if (value == 0) continue;
                foreach (var (source, conversion, colour, gemColour) in MinionSocketConversions)
                {
                    if (source != key) continue;
                    int count = item.Sockets.Count(socket => !gemColour ? socket.Colour == colour
                        : colour == SocketColour.White ? socket.Colour != SocketColour.Abyss && socket.Gem is null
                        : socket.Colour != SocketColour.Abyss && socket.Gem is { } gem
                            && catalog.ByHash(gem.TypeHash) is { } type && catalog.Gem(type)?.Colour == (int)colour);
                    GolemAdd(stats, conversion, ActorScale.Saturate((long)value * count));
                    break;
                }
            }
        }
    }
}
