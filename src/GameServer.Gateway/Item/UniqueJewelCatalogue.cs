// Generated from Path of Building src/Export/Uniques/jewel.lua for the target 3.29.3b data.
// Do not edit by hand; regenerate with scripts/re/unique_jewels.py (a read-only third-party reference).
// Global jewels have no radius and no conditional mechanic; Special jewels have an item source but a
// mechanic that is not modelled yet.

/// <summary>The non-radius unique jewels: base type and mod HASH16s. Global entries are unconditional;
/// Special entries are item sources whose mechanic remains partial/inert. The unique art of every jewel is
/// in <see cref="UniqueJewelVisuals"/>.</summary>
static class UniqueJewelCatalogue
{
    public sealed record Entry(string Name, string Base, bool Corrupted, int Level,
        string[] Mods, int?[] Values);

    public static Entry[] Global => GameDataCatalog.UniqueJewelGlobals;
    public static Entry[] Special => GameDataCatalog.UniqueJewelSpecials;

    public static Entry? Find(string name) =>
        Global.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Special.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string[] Names =>
        [.. Global.Concat(Special).Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
}
