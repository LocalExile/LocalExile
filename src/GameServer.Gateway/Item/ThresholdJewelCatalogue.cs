// Generated from Path of Building src/Export/Uniques/jewel.lua for the target 3.29.3b data.
// Do not edit by hand; regenerate with scripts/re/threshold_jewels.py.

/// <summary>The 33 threshold jewels: item name, base, limits and the client mods. The mods are
/// <c>local_unique_jewel_..._with_40_&lt;attribute&gt;_in_radius</c> (or the 50-attribute variants); the
/// client applies them to the passive tree, while the server's flat stat model only consumes the
/// unconditional global mods. ReadConversion therefore returns null for these items.</summary>
public static class ThresholdJewelCatalogue
{
    public static RadiusJewelCatalogue.Entry[] All => GameDataCatalog.ThresholdJewels;
}
