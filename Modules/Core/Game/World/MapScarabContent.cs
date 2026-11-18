using System.Text.RegularExpressions;
using GameServer.Modding;

/// <summary>Named scarab content requests, distinct from the map's ordinary numeric modifiers.</summary>
public static class MapScarabContent
{
    public const string StandardShrines = "Shrines.Standard", Sulphite = "Sulphite";

    static readonly Lazy<MapRunCatalog> Fragments = new(() => MapRunCatalog.Load("data"));
    static readonly Regex Family = new(@"^Scarab(?<family>[A-Za-z]+?)\d", RegexOptions.Compiled);
    static readonly IReadOnlyDictionary<string, string> Encounters = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Breach"] = "Breach", ["Beasts"] = "Bestiary", ["Torment"] = "Torment",
        ["Strongbox"] = "Ambush", ["Harbinger"] = "Harbinger", ["Perandus"] = "Expedition",
        ["Expedition"] = "Expedition", ["Legion"] = "Legion", ["Metamorph"] = "Ultimatum",
        ["Blight"] = "Blight", ["Abyss"] = "Abyss", ["Anarchy"] = "RogueExiles",
        ["Essence"] = "Essence", ["Ritual"] = "Ritual", ["Harvest"] = "SacredGrove",
        ["Incursion"] = "Incursion", ["Betrayal"] = "Betrayal", ["Beyond"] = "Beyond",
        ["Ultimatum"] = "Ultimatum", ["Delirium"] = "DeliriumMirror", ["Settlers"] = "KalguuranOre",
        ["Mercenaries"] = "Mercenaries", ["ShaperRares"] = "ShaperPacks", ["ElderRares"] = "ElderPacks",
        ["Influence"] = "InfluencedPacks", ["Sulphite"] = Sulphite,
    };

    static string? Encounter(string family, MapMods mods)
    {
        if (family.EndsWith("New", StringComparison.Ordinal)) family = family[..^3];
        if (family == "Domination")
            return mods.Get("map_num_extra_apparition_shrines") > 0 ? "Shrines.Apparitions"
                : mods.Get("map_num_extra_evolving_shrines") > 0 ? "Shrines.Evolving"
                : mods.Get("map_shrines_have_standalone_map_boss") > 0 ? MapScarabBosses.Terrors : StandardShrines;
        if (family == "Strongbox")
            return mods.Get("map_strongbox_close_after_opening_chance_%") > 0 ? MapStrongboxes.Reopen
                : mods.Get("map_strongbox_mod_effect_+%") > 0 ? "Ambush.Potency"
                : mods.Get("map_monstrous_treasure_no_monsters") > 0 ? MapStrongboxes.Containment
                : mods.Get("map_strongbox_rare_boxes_weighting_multiplier") > 0 ? MapStrongboxes.Discernment : MapStrongboxes.Content;
        if (family == "Essence")
            return mods.Get("map_stable_essence_corruption") > 0 ? "Essence.Stability"
                : mods.Get("map_rare_monsters_trapped_in_essences") > 0 ? MapEssences.Calcification
                : mods.Get("map_essence_imprisoned_monsters_enhanced_for_each_essence") > 0 ? "Essence.Adaptation"
                : mods.Get("map_essences_are_1_tier_higher_chance_%") > 0 ? MapEssences.Ascent : MapEssences.Content;
        if (mods.Get(MapScarabPacks.CountStat) > 0) return MapScarabPacks.Cloister;
        if (mods.Get("map_atlas_boss_pack_replacement_chance_%") > 0) return "AtlasBossPacks";
        return Encounters.GetValueOrDefault(family);
    }

    public static void Describe(MapOpening request)
    {
        if (request.Opening is not { } opening || request.Campaign) return;
        foreach (var slot in request.Slots)
        {
            if (slot.IsMap || Program.Items.ByHash(slot.TypeHash) is not { } type) continue;
            var family = Family.Match(type.Id.Split('/')[^1]);
            if (!family.Success) continue;
            var mods = Fragments.Value.Of([slot], Program.Items.Mods);
            if (Encounter(family.Groups["family"].Value, mods) is not { } content) continue;
            string values = string.Join(", ", mods.Stats.OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => $"{s.Key}={s.Value}"));
            request.ContentWarnings.Add((content,
                $"area={opening.Area.Id} owner={request.Conn.Character} scarab={type.Id} ({type.Name}) "
                + $"content={content} stats=[{values}]: no enabled provider implements this named encounter effect"));
        }
    }
}
