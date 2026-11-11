using GameServer.Core.Protocol.Instance;

public static class InspiredLearning
{
    const string FourNotables = "local_unique_jewel_with_4_notables_gain_X_random_rare_monster_mods_on_kill";
    const string NoNotables = "local_unique_jewel_with_no_notables_gain_X_random_rare_monster_mods_on_kill";
    const string SmallPassives = "local_unique_jewel_with_x_small_nodes_gain_1_random_rare_monster_mods_on_kill";

    public static int Granted(Item item, ModCatalog mods, PassiveTree tree, ushort socket, IReadOnlySet<ushort> allocated)
    {
        var local = ItemModStats.Local(item, mods);
        int four = local.GetValueOrDefault(FourNotables), none = local.GetValueOrDefault(NoNotables);
        int smallNeeded = local.GetValueOrDefault(SmallPassives);
        if ((four | none | smallNeeded) == 0 || !allocated.Contains(socket)) return 0;
        int notables = 0, small = 0;
        foreach (ushort node in tree.NodesInRadius(socket, local.GetValueOrDefault("local_jewel_effect_base_radius")))
            if (allocated.Contains(node) && tree.Find(node) is { IsAscendancy: false, IsStart: false, IsProxy: false,
                IsExpansion: false, IsMastery: false, IsJewelSocket: false, IsKeystone: false } passive)
            {
                if (passive.IsNotable) notables++;
                else small++;
            }
        return (notables >= 4 ? four : 0) + (notables == 0 ? none : 0) + (smallNeeded > 0 && small >= smallNeeded ? 1 : 0);
    }
}
