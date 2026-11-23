namespace GameServer.Gateway.Tests;

/// <summary>The Atlas tree's own rules on its stats (AtlasStats): Wandering Path, Grand Design and the mod effect per fragment.</summary>
public sealed class AtlasStatsTests
{
    static uint K(string id) => Program.Stats.Key(id)!.Value;

    static AtlasPassives.Node Node(string id, bool notable, bool keystone, params (string Id, int Value)[] stats) =>
        new(0, id, id, notable, keystone, [.. stats.Select(s => (K(s.Id), s.Value))]);

    [Fact]
    public void ComposeAppliesTheKeystonesAndTheModEffect()
    {
        const string quantity = "map_item_drop_quantity_+%", perFragment = "map_mod_effect_+%_per_fragment_used";
        var small = Node("atlas_map_1_1", false, false, (quantity, 3));
        var breach = Node("atlas_breach_1_1", false, false, (quantity, 1));
        var fragments = Node("atlas_mod_effect_notable_1", true, false, (perFragment, 3));
        var gateway = new AtlasPassives.Node(0, "atlas_wormhole_1_1", "", false, false,
            [(K("atlas_number_of_gateways_allocated"), 1)], IsWormhole: true);
        var wandering = Node("atlas_keystone_smallnodes_1", false, true,
            ("display_notable_atlas_passives_grant_nothing", 1), ("small_atlas_passive_effect_+%", 100));
        var unfettered = Node("atlas_keystone_all_in_on_one_league", false, true, ("map_atlas_extra_content_small_node_effect_+%", 100));
        var grand = Node("atlas_keystone_notables_1_", false, true,
            ("display_small_atlas_passives_grant_nothing", 1), ("map_pack_size_+%_per_atlas_notable_passive_allocated", 1));

        // Wandering Path: Small nodes doubled (an Extra Content one tripled with Unfettered Concentration), Notables nothing,
        // the Gateway as it is.
        var wanderingPath = AtlasStats.Compose([small, breach, fragments, gateway, wandering, unfettered]);
        Assert.Equal(3 * 2 + 1 * 3, wanderingPath[K(quantity)]);
        Assert.False(wanderingPath.ContainsKey(K(perFragment)));
        Assert.Equal(1, wanderingPath[K("atlas_number_of_gateways_allocated")]);
        Assert.Equal(0, AtlasStats.ModEffect(wanderingPath, false, 4, 2));

        // Grand Design: Small nodes nothing, 1% pack size per allocated Notable; 3% mod effect per fragment, unique or not.
        var grandDesign = AtlasStats.Compose([small, fragments, Node("atlas_boss_1_3", true, false, (quantity, 5)), grand]);
        Assert.Equal(5, grandDesign[K(quantity)]);
        Assert.Equal(2, grandDesign[K("map_pack_size_+%")]);
        Assert.Equal(9, AtlasStats.ModEffect(grandDesign, true, 4, 3));
    }
}
