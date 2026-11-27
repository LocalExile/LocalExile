using System.Text.RegularExpressions;
using GameServer.Modding;

/// <summary>Named scarab content requests, distinct from the map's ordinary numeric modifiers.</summary>
public static class MapScarabContent
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        Fragments = LoadFragments();
    }

    public const string StandardShrines = "Shrines.Standard", Sulphite = "Sulphite",
        KalguuranOre = "KalguuranOre", KalguuranGuardedRiches = "KalguuranOre.GuardedRiches",
        KalguuranRefinement = "KalguuranOre.Refinement", KalguuranEnriching = "KalguuranOre.Enriching";

    static MapRunCatalog Fragments = default!;
    static MapRunCatalog LoadFragments() => MapRunCatalog.Load("data");
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
            return mods.Get(Stat.MapNumExtraApparitionShrines) > 0 ? "Shrines.Apparitions"
                : mods.Get(Stat.MapNumExtraEvolvingShrines) > 0 ? "Shrines.Evolving"
                : mods.Get(Stat.MapShrinesHaveStandaloneMapBoss) > 0 ? MapScarabBosses.Terrors : StandardShrines;
        if (family == "Strongbox")
            return mods.Get(Stat.MapStrongboxCloseAfterOpeningChancePct) > 0 ? MapStrongboxes.Reopen
                : mods.Get(Stat.MapStrongboxModEffectPlusPct) > 0 ? "Ambush.Potency"
                : mods.Get(Stat.MapMonstrousTreasureNoMonsters) > 0 ? MapStrongboxes.Containment
                : mods.Get(Stat.MapStrongboxRareBoxesWeightingMultiplier) > 0 ? MapStrongboxes.Discernment : MapStrongboxes.Content;
        if (family == "Essence")
            return mods.Get(Stat.MapStableEssenceCorruption) > 0 ? "Essence.Stability"
                : mods.Get(Stat.MapRareMonstersTrappedInEssences) > 0 ? MapEssences.Calcification
                : mods.Get(Stat.MapEssenceImprisonedMonstersEnhancedForEachEssence) > 0 ? "Essence.Adaptation"
                : mods.Get(Stat.MapEssencesAre1TierHigherChancePct) > 0 ? MapEssences.Ascent : MapEssences.Content;
        if (family == "Delirium" && mods.Get(Stat.MapMapsDropWithAfflictionEnchantmentsChancePct) > 0) return "Delirium.Delusions";
        if (family == "Settlers")
            return mods.Get(Stat.MapAllVillagePacksMagicAndAllowRareChancePct) > 0 ? KalguuranGuardedRiches
                : mods.Get(Stat.MapVillageOreFoundAsBarsInstead) > 0 ? KalguuranRefinement
                : mods.Get(Stat.MapVillageEscalatingEncounters) > 0 ? KalguuranEnriching : KalguuranOre;
        if (mods.Get(MapScarabPacks.CountStat) > 0) return MapScarabPacks.Cloister;
        if (mods.Get(Stat.MapAtlasBossPackReplacementChancePct) > 0) return "AtlasBossPacks";
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
            var mods = Fragments.Of([slot], Program.Items.Mods);
            if (Encounter(family.Groups["family"].Value, mods) is not { } content) continue;
            string values = string.Join(", ", mods.Stats.Select(s => (Name: Program.Stats.Name(s.Key), s.Value))
                .OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => $"{s.Name}={s.Value}"));
            request.ContentWarnings.Add((content,
                $"area={opening.Area.Id} owner={request.Player.Character.Name} scarab={type.Id} ({type.Name}) "
                + $"content={content} stats=[{values}]: no enabled provider implements this named encounter effect"));
        }
    }
}
