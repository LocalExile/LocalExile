using GameServer.Core.Protocol.Instance;

public static class InspiredLearning
{
    const uint FourNotables = Stat.LocalUniqueJewelWith4NotablesGainXRandomRareMonsterModsOnKill;
    const uint NoNotables = Stat.LocalUniqueJewelWithNoNotablesGainXRandomRareMonsterModsOnKill;
    const uint SmallPassives = Stat.LocalUniqueJewelWithXSmallNodesGain1RandomRareMonsterModsOnKill;

    public static int Granted(Item item, ModCatalog mods, PassiveTree tree, ushort socket, IReadOnlySet<ushort> allocated)
    {
        var local = ItemModStats.Local(item, mods);
        int four = local.GetValueOrDefault(FourNotables), none = local.GetValueOrDefault(NoNotables);
        int smallNeeded = local.GetValueOrDefault(SmallPassives);
        if ((four | none | smallNeeded) == 0 || !allocated.Contains(socket)) return 0;
        int notables = 0, small = 0;
        foreach (ushort node in tree.NodesInRadius(socket, local.GetValueOrDefault(Stat.LocalJewelEffectBaseRadius)))
            if (allocated.Contains(node) && tree.Find(node) is { IsAscendancy: false, IsStart: false, IsProxy: false,
                IsExpansion: false, IsMastery: false, IsJewelSocket: false, IsKeystone: false } passive)
            {
                if (passive.IsNotable) notables++;
                else small++;
            }
        return (notables >= 4 ? four : 0) + (notables == 0 ? none : 0) + (smallNeeded > 0 && small >= smallNeeded ? 1 : 0);
    }
}
