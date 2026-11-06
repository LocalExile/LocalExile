using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AreaStatsTests
{
    // A real 3.29 map activation the server corpus holds (Jungle Valley at seed 3410467983,
    // 94 area stats): a corrupted rare tier-16 map with four explicit mods, two Sacrifice fragments and two Settlers
    // scarabs, a 129-node atlas preset (the second) with a 24% map mod effect. The composition reproduces every stat
    // except the account block (the atlas completion quantity bonus of 25, the voidstone / Maven / astrolabe / uber map
    // / gold conversion unlocks, the tier bonus permillage and the phrecian flag), the activated atlas node, the boss
    // option's four stats and the server's Beyond roll - listed below as what the Gateway does not model yet.
    [Fact]
    public void AMapLoadsAreaStatsAreComposedFromItsInputs()
    {
        var catalog = new StatCatalog("data");
        if (!catalog.Has("map_tier")) return; // needs data/tables/Stats.json
        Dictionary<string, int> Mod(params (string Id, int Value)[] stats) => stats.ToDictionary(s => s.Id, s => s.Value);
        var explicitMods = new List<IReadOnlyDictionary<string, int>>
        {
            Mod(("map_item_drop_quantity_+%", 13), ("map_item_drop_rarity_+%", 8), ("map_monsters_spell_suppression_chance_%", 60), ("map_pack_size_+%", 5)),
            Mod(("map_item_drop_quantity_+%", 13), ("map_item_drop_rarity_+%", 8), ("map_monsters_curse_effect_on_self_+%_final", -60), ("map_pack_size_+%", 5)),
            Mod(("map_item_drop_quantity_+%", 13), ("map_item_drop_rarity_+%", 8), ("map_pack_size_+%", 5), ("map_player_has_level_X_temporal_chains", 15)),
            Mod(("map_item_drop_quantity_+%", 13), ("map_item_drop_rarity_+%", 8), ("map_pack_size_+%", 5), ("map_players_armour_+%_final", -30), ("map_players_block_chance_+%", -40)),
        };
        var fragments = Mod(("map_all_village_packs_magic_and_allow_rare_chance_%", 100), ("map_contains_X_additional_village_ores", 2), ("map_force_side_area", 2),
            ("map_item_drop_quantity_+%", 10), ("map_sacrifice_fragment_count", 2));
        var mods = new MapMods(new Dictionary<string, int>())
        {
            ExplicitMods = explicitMods, Fragments = fragments, ExplicitCount = 4, FragmentCount = 4, ScarabCount = 2,
            ScarabKinds = Mod(("settlers", 2)), MapRarity = ItemRarity.Rare, Corrupted = true,
        };
        var atlasByName = Mod(("atlas_keystone_cassias_pride", 1), ("blighted_map_chest_reward_lucky_count", 6), ("map_+%_increased_scarab_drops", 49), ("map_alva_cursed_treasure", 1),
            ("map_beyond_basic_currency_quantity_+%_final_from_pale_faction", 30), ("map_beyond_boss_spawn_chance_+%", 30), ("map_beyond_demon_faction_chance_+%", 120),
            ("map_beyond_divination_card_quantity_+%_final_from_demon_faction", 30), ("map_beyond_flesh_faction_chance_+%", 120), ("map_beyond_pale_faction_chance_+%", 120),
            ("map_beyond_portal_chance_+%", 15), ("map_beyond_portal_search_radius_+%", 16), ("map_beyond_rules_chance_%", 80),
            ("map_beyond_unique_item_quantity_+%_final_from_flesh_faction", 30), ("map_blight_chest_%_chance_for_additional_drop", 16),
            ("map_blight_chest_close_after_opening_chance_%", 20), ("map_blight_encounter_additional_chance_%", 100), ("map_blight_encounter_blighted_map_reward_chance_+%", 100),
            ("map_blight_lane_additional_chest_chance_%", 5), ("map_blight_monster_item_quantity_+%", 22), ("map_blight_monsters_take_+%_damage", 36),
            ("map_blight_scarab_more_likely_%", 100), ("map_blight_touched_buff_on_blight_encounter_complete", 1), ("map_blight_tower_cost_+%", -40),
            ("map_blight_towers_are_chests", 1), ("map_extra_incursion_per_area", 1), ("map_inchor_pump_has_double_durability", 1),
            ("map_incursion_add_room_tier_to_other_architects_room_when_killing_architect", 1), ("map_incursion_all_monster_packs_magic_and_allow_rare_chance_%", 33),
            ("map_incursion_chance_for_additional_upgrade_when_killing_non_resident_architect_%", 50), ("map_incursion_kill_time_doubled", 1), ("map_incursion_pack_size_+%", 20),
            ("map_incursion_scarab_more_likely_%", 104), ("map_incursions_spawn_special_monster", 1), ("map_item_drop_quantity_+%", 2), ("map_item_drop_rarity_+%", 4),
            ("map_map_item_drop_chance_+%", 14), ("map_master_mission_alva_%_chance", 104), ("map_mod_effect_+%_for_non_unique_maps", 24),
            ("map_rare_monster_scarab_drops_+%_per_modifier", 50), ("map_settlers_%_chance_for_lost_shipment_instead_of_ore", 6), ("map_settlers_bismuth_additional_modifier_%_chance", 30),
            ("map_settlers_ore_additional_chance_%", 90), ("map_settlers_ore_found_+%", 90), ("map_settlers_scarab_more_likely_%", 90), ("map_settlers_verisium_maximum_channel_+%", 30),
            ("map_unique_monster_scarab_drops_+%", 200), ("map_village_bismuth_ore_chance_+%", 100), ("map_village_rarer_ores_found", 1), ("map_village_secondary_ore_chance_+%", 50),
            ("map_village_verisium_ore_chance_+%", 100));
        var atlas = atlasByName.ToDictionary(p => catalog.Key(p.Key) ?? throw new Xunit.Sdk.XunitException(p.Key), p => p.Value);
        var families = new HashSet<string> { "alva", "beyond", "blight", "keystone", "map", "mod", "path", "scarab", "settlers" };

        var composed = AreaStats.Compose(mods, 16, atlas, families, preset: 1, deepwater: true, catalog);

        var area = Mod(("atlas_keystone_cassias_pride", 1), ("blight_path_piece_length_modifier_%", 20), ("blighted_map_chest_reward_lucky_count", 6),
            ("map_+%_increased_scarab_drops", 49), ("map_active_atlas_passive_tree_index", 2), ("map_all_village_packs_magic_and_allow_rare_chance_%", 100),
            ("map_alva_cursed_treasure", 1), ("map_atlas_information", 6928), ("map_beyond_basic_currency_quantity_+%_final_from_pale_faction", 30),
            ("map_beyond_boss_spawn_chance_+%", 30), ("map_beyond_demon_faction_chance_+%", 120), ("map_beyond_divination_card_quantity_+%_final_from_demon_faction", 30),
            ("map_beyond_flesh_faction_chance_+%", 120), ("map_beyond_pale_faction_chance_+%", 120), ("map_beyond_portal_chance_+%", 15), ("map_beyond_portal_search_radius_+%", 16),
            ("map_beyond_rules_chance_%", 80), ("map_beyond_unique_item_quantity_+%_final_from_flesh_faction", 30), ("map_blight_chest_%_chance_for_additional_drop", 16),
            ("map_blight_chest_close_after_opening_chance_%", 20), ("map_blight_encounter_additional_chance_%", 100), ("map_blight_encounter_blighted_map_reward_chance_+%", 100),
            ("map_blight_lane_additional_chest_chance_%", 5), ("map_blight_monster_item_quantity_+%", 22), ("map_blight_monsters_damage_taken_from_player_sources_+%_final", -75),
            ("map_blight_monsters_take_+%_damage", 36), ("map_blight_scarab_more_likely_%", 100), ("map_blight_touched_buff_on_blight_encounter_complete", 1),
            ("map_blight_tower_cost_+%", -40), ("map_blight_tower_damage_+%_final_from_atlas_passive", 300), ("map_blight_towers_are_chests", 1),
            ("map_contains_X_additional_village_ores", 2), ("map_deepwater_league", 1), ("map_device_type", 1), ("map_extra_incursion_per_area", 1), ("map_force_side_area", 2),
            ("map_hidden_monster_damage_+%_final", 15), ("map_hidden_monster_life_+%_final", 109), ("map_inchor_pump_has_double_durability", 1),
            ("map_incursion_add_room_tier_to_other_architects_room_when_killing_architect", 1), ("map_incursion_all_monster_packs_magic_and_allow_rare_chance_%", 33),
            ("map_incursion_chance_for_additional_upgrade_when_killing_non_resident_architect_%", 50), ("map_incursion_kill_time_doubled", 1), ("map_incursion_league", 1),
            ("map_incursion_pack_size_+%", 20), ("map_incursion_scarab_more_likely_%", 104), ("map_incursions_spawn_special_monster", 1), ("map_is_corrupted", 1),
            ("map_item_drop_quantity_+%", 101 - 25), ("map_item_drop_rarity_+%", 40), ("map_map_item_drop_chance_+%", 14), ("map_master_mission_alva_%_chance", 104),
            ("map_mod_effect_+%_for_non_unique_maps", 24), ("map_monsters_curse_effect_on_self_+%_final", -74), ("map_monsters_spell_suppression_chance_%", 74),
            ("map_num_fragments", 4), ("map_num_scarabs", 2), ("map_number_of_explicit_mods", 4), ("map_pack_size_+%", 24), ("map_player_has_level_X_temporal_chains", 18),
            ("map_players_armour_+%_final", -37), ("map_players_block_chance_+%", -49), ("map_rare_monster_scarab_drops_+%_per_modifier", 50), ("map_rarity", 2),
            ("map_sacrifice_fragment_count", 2), ("map_settlers_%_chance_for_lost_shipment_instead_of_ore", 6), ("map_settlers_bismuth_additional_modifier_%_chance", 30),
            ("map_settlers_ore_additional_chance_%", 90), ("map_settlers_ore_found_+%", 90), ("map_settlers_scarab_count", 2), ("map_settlers_scarab_more_likely_%", 90),
            ("map_settlers_verisium_maximum_channel_+%", 30), ("map_tier", 16), ("map_total_scarab_tiers", 2), ("map_unique_monster_scarab_drops_+%", 200),
            ("map_village_bismuth_ore_chance_+%", 100), ("map_village_rarer_ores_found", 1), ("map_village_secondary_ore_chance_+%", 50), ("map_village_verisium_ore_chance_+%", 100),
            ("total_number_of_map_mods", 8));
        // Not modelled: the account block, the activated node, the boss option and the Beyond roll (above).
        var expected = area.ToDictionary(p => catalog.Key(p.Key) ?? throw new Xunit.Sdk.XunitException(p.Key), p => p.Value);
        var differences = expected.Keys.Union(composed.Keys)
            .Where(k => !expected.TryGetValue(k, out int e) || !composed.TryGetValue(k, out int c) || e != c)
            .Select(k => $"{catalog.Name(k)}: area {(expected.TryGetValue(k, out int e) ? e : "-")}, composed {(composed.TryGetValue(k, out int c) ? c : "-")}")
            .ToList();
        Assert.True(differences.Count == 0, string.Join("; ", differences));
    }
}
