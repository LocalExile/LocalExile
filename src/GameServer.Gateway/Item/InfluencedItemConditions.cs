using GameServer.Core.Protocol.Instance;

public static class InfluencedItemConditions
{
    static readonly IReadOnlyDictionary<string, ItemInfluence> Influences = new Dictionary<string, ItemInfluence>
    {
        ["shaper"] = ItemInfluence.Shaper, ["elder"] = ItemInfluence.Elder, ["crusader"] = ItemInfluence.Crusader,
        ["redeemer"] = ItemInfluence.Redeemer, ["hunter"] = ItemInfluence.Hunter, ["warlord"] = ItemInfluence.Warlord,
    };

    // Nine actual EquippedItem conditions for each influence: three possible effects at each 2/4/6 threshold.
    public static bool Read(string id, out string target, out ItemInfluence influence, out int required)
    {
        target = id;
        influence = ItemInfluence.None;
        required = 0;
        if (!id.EndsWith("_items", StringComparison.Ordinal)) return false;
        int boundary = id.LastIndexOf("_if_", StringComparison.Ordinal);
        if (boundary < 0 || boundary + 4 >= id.Length - "_items".Length) return false;
        var condition = id.AsSpan(boundary + 4, id.Length - boundary - 4 - "_items".Length);
        if (condition.Length < 3 || condition[1] != '_' || condition[0] is not ('2' or '4' or '6')
            || !Influences.TryGetValue(condition[2..].ToString(), out influence)) return false;
        target = id[..boundary];
        required = condition[0] - '0';
        return true;
    }

    public static IEnumerable<string> Targets(string target) => target switch
    {
        "additional_projectile" => ["number_of_additional_projectiles"],
        "physical_damage_%_to_add_as_each_element" =>
            ["physical_damage_%_to_add_as_fire", "physical_damage_%_to_add_as_cold", "physical_damage_%_to_add_as_lightning"],
        "elemental_damage_%_taken_as_chaos" =>
            ["elemental_damage_taken_%_as_chaos"],
        _ => [target],
    };
}
