using GameServer.Core.Protocol.Instance;

partial class Program
{
    // Conditions use active equipment, not inventory contents or the inactive weapon set. An empty slot has no gems.
    static Dictionary<uint, int> EquipmentConditions(ClientState conn, Dictionary<uint, int> sources)
    {
        int Get(string name) => Stats.Key(name) is uint key ? sources.GetValueOrDefault(key) : 0;
        void Add(string name, int value)
        {
            if (value != 0 && Stats.Key(name) is uint key) sources[key] = sources.GetValueOrDefault(key) + value;
        }
        var inventory = ItemsOf(conn);
        bool Empty(byte row) => inventory.Items(row).Where(i => !IsInactive(conn, i.Id))
            .All(i => i.Item.Sockets.All(s => s.Gem is null));
        if (Empty(PlayerInventories.Helmet)) Add("base_resist_all_elements_%", Get("elemental_resistance_%_while_no_gems_in_helmet"));
        if (Empty(PlayerInventories.Gloves)) Add("maximum_life_+%", Get("maximum_life_+%_while_no_gems_in_gloves"));
        if (Empty(PlayerInventories.Boots)) Add("base_movement_velocity_+%", Get("movement_velocity_+%_while_no_gems_in_boots"));
        int corrupted = ItemModStats.EquipmentRows.SelectMany(inventory.Items)
            .Count(i => !IsInactive(conn, i.Id) && i.Item.Corrupted);
        Add("maximum_life_+%", corrupted * Get("maximum_life_+%_per_equipped_corrupted_item"));
        Add("maximum_energy_shield_+%", corrupted * Get("maximum_es_+%_per_equipped_corrupted_item"));
        int resistance = corrupted * Get("all_resistances_%_per_equipped_corrupted_item");
        Add("base_resist_all_elements_%", resistance);
        Add("base_chaos_damage_resistance_%", resistance);
        return sources;
    }
}
