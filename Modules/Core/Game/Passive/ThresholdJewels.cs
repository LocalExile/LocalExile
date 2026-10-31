using GameServer.Core.Protocol.Instance;


/// <summary>
/// The server side of the threshold jewels. Each threshold mod names the attribute mask and the required total
/// (<c>local_unique_jewel_..._with_40_dex_in_radius</c>), and the stat to grant. Path of Building's
/// <c>getThreshold</c> sums the attributes of every ordinary passive in radius, allocated or not, and adds the
/// effect once the total reaches the threshold; the client applies the same local mods itself.
///
/// The stat keys are the client's skill stats; the server's damage consumers currently read only some of them.
/// Unknown targets (for example the <c>*_final</c> more-multiplier rows without a matching stat) are skipped.
/// </summary>
static class ThresholdJewels
{
    [Flags]
    public enum Attributes
    {
        None = 0,
        Strength = 1,
        Dexterity = 2,
        Intelligence = 4,
    }

    public sealed record Effect(Attributes Attributes, int Required, uint Key, int Value);

    /// <summary>A stat whose local name does not match its client stat key, or whose local value is a flag/ms
    /// value that has to be converted.</summary>
    sealed record Alias(string Target, int? FixedValue = null);

    static readonly IReadOnlyDictionary<string, Alias> Aliases = new Dictionary<string, Alias>
    {
        ["disable_combust"] = new("combust_is_disabled", 1),
        ["frostbolt_additional_projectiles"] = new("frostbolt_number_of_additional_projectiles"),
        ["ice_shot_explosion_skill_area_of_effect_+%"] = new("ice_shot_radius_+%"),
        ["spectres_gain_soul_eater_on_kill_%_chance"] =
            new("spectres_gain_soul_eater_for_20_seconds_on_kill_%_chance"),
        ["molten_strike_projectiles_chain_count_+"] = new("molten_strike_chain_count_+"),
        ["molten_strike_projectiles_count_+%_final"] =
            new("threshold_jewel_molten_strike_damage_projectile_count_+%_final"),
        ["cold_snap_uses_gains_power_charges_instead_of_frenzy"] =
            new("cold_snap_uses_and_gains_power_charges_instead_of_frenzy", 1),
        ["vigilant_strike_fortifies_nearby_allies_for_x_seconds"] =
            new("vigilant_strike_applies_to_nearby_allies_for_X_seconds"),
    };

    /// <summary>The threshold effects on one item and the radius those effects use.</summary>
    public static (int Radius, IReadOnlyList<Effect> Effects) Read(Item item, ModCatalog mods)
    {
        var effects = new List<Effect>();
        int radius = 0;
        foreach (var mod in item.Mods)
        {
            if (mods.ByHash(mod.Hash) is not { } row) continue;
            for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
            {
                string id = row.Stats[i].Id;
                if (id == "local_jewel_effect_base_radius")
                {
                    radius = mod.Values[i];
                    continue;
                }
                if (!TryParse(id, out string target, out Attributes attributes, out int required)) continue;
                int value = mod.Values[i];
                if (Aliases.TryGetValue(target, out var alias))
                {
                    target = alias.Target;
                    value = alias.FixedValue ?? value;
                }
                if (value == 0 || Program.Stats.Key(target) is not uint key) continue;
                effects.Add(new(attributes, required, key, value));
            }
        }
        return (radius > 0 ? radius : 1200, effects);
    }

    /// <summary>Whether the summed attributes in radius reach this effect's threshold.</summary>
    public static bool Met(Effect effect, IReadOnlyDictionary<uint, int> attributes)
    {
        int total = 0;
        if ((effect.Attributes & Attributes.Strength) != 0)
            total += Attribute(attributes, "base_strength");
        if ((effect.Attributes & Attributes.Dexterity) != 0)
            total += Attribute(attributes, "base_dexterity");
        if ((effect.Attributes & Attributes.Intelligence) != 0)
            total += Attribute(attributes, "base_intelligence");
        return total >= effect.Required;
    }

    static int Attribute(IReadOnlyDictionary<uint, int> attributes, string name) =>
        Program.Stats.Key(name) is uint key ? attributes.GetValueOrDefault(key) : 0;

    /// <summary>Split a local threshold stat into its target and condition. The condition is the last
    /// <c>_with_..._in_radius</c> segment, so targets such as <c>..._with_cold_damage</c> stay intact.</summary>
    static bool TryParse(string id, out string target, out Attributes attributes, out int required)
    {
        target = "";
        attributes = Attributes.None;
        required = 0;
        if (!id.StartsWith("local_", StringComparison.Ordinal) ||
            !id.EndsWith("_in_radius", StringComparison.Ordinal)) return false;
        string rest = id.StartsWith("local_unique_jewel_", StringComparison.Ordinal)
            ? id["local_unique_jewel_".Length..]
            : id.StartsWith("local_jewel_", StringComparison.Ordinal) ? id["local_jewel_".Length..] : "";
        int at = rest.LastIndexOf("_with_", StringComparison.Ordinal);
        if (at < 0) return false;
        target = rest[..at];
        string condition = rest[(at + "_with_".Length)..^"_in_radius".Length];
        if (condition.Contains("str", StringComparison.Ordinal) ||
            condition.Contains("strength", StringComparison.Ordinal)) attributes |= Attributes.Strength;
        if (condition.Contains("dex", StringComparison.Ordinal) ||
            condition.Contains("dexterity", StringComparison.Ordinal)) attributes |= Attributes.Dexterity;
        if (condition.Contains("int", StringComparison.Ordinal) ||
            condition.Contains("intelligence", StringComparison.Ordinal)) attributes |= Attributes.Intelligence;
        if (attributes == Attributes.None) return false;
        // The Infernal Cry line says "for Strength" and its text is the usual 40 Strength.
        if (condition == "for_strength") required = 40;
        foreach (char c in condition)
        {
            if (!char.IsAsciiDigit(c)) continue;
            required = required * 10 + (c - '0');
        }
        return required > 0;
    }
}
