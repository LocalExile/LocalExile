

/// <summary>
/// Areas whose unique boss the server picks per character: a WorldAreas row whose mods include a MapBossOverride… mod
/// (stat map_boss_override) lists every candidate in Bosses_MonsterVarietiesKeys, and one of them stands at the area's
/// "mapboss" point (Agents/docs/protocol/14-monster-packs.md §3).
/// <para>The Ashen Fields (2_7_7, MapBossOverrideAshenFields; Bosses Greust, Oak, Alira, Kraityn of
/// Metadata/Monsters/Ralakesh/): Ralakesh's avatar is the bandit lord the character spared in Act 2, the one whose
/// a2b-Killed…Bandit flag is missing while the other two are set (Eramir's EramirRalakeshQuestOak/…Alira/…Kraityn and
/// Helena's HelenaOn…Quest lines: Greust died standing against that lord), and Greust when all three were killed
/// (EramirRalakeshQuestGreust). Any other combination (no bandit quest done, as with /goto) keeps Greust. Nothing here
/// sets those flags but /quest_flag (a quest engine sets them on the bandit lords' kills), and the instance's boss
/// follows the first character to enter it.</para>
/// </summary>
static class MapBossOverride
{
    const string AshenFields = "2_7_7", Ralakesh = "Metadata/Monsters/Ralakesh/";

    // Each bandit lord's kill flag (a2b: Kraityn Dex, Alira Int, Oak Str) and Ralakesh form.
    static readonly (string Killed, string Variety)[] Lords =
    [
        ("a2b-KilledStrBandit", Ralakesh + "Oak/Oak"),
        ("a2b-KilledIntBandit", Ralakesh + "Alira/Alira"),
        ("a2b-KilledDexBandit", Ralakesh + "Kraityn/Kraityn"),
    ];

    /// <summary>The area with the bosses this character does not meet marked absent; other areas unchanged.</summary>
    public static PackCatalog.AreaPacks Apply(PackCatalog.AreaPacks area, IReadOnlySet<string> flags, Func<int, string?> varietyId)
    {
        if (area.AreaId != AshenFields) return area;
        var spared = Lords.Where(l => !flags.Contains(l.Killed)).ToList();
        string boss = spared.Count == 1 ? spared[0].Variety : Ralakesh + "Greust/Greust";
        var others = Enumerable.Range(0, area.Bosses.Count).Where(i => varietyId(area.Bosses[i]) != boss).ToHashSet();
        // A table without that boss: leave the area as it is rather than without a boss.
        return others.Count == area.Bosses.Count ? area : area with { Absent = area.Absent.Union(others).ToHashSet() };
    }
}
