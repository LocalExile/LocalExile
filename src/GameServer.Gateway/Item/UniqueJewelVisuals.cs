// Generated from the client's UniqueStashLayout, Words and ItemVisualIdentity for the target 3.29.3b data.
// Do not edit by hand; regenerate with scripts/re/unique_jewels.py.

/// <summary>The unique art of every unique jewel the stash layout lists, by name: the ItemVisualIdentity
/// HASH16 the client draws for the item. A jewel whose name is missing has no art of its own.</summary>
public static class UniqueJewelVisuals
{
    public static IReadOnlyDictionary<string, ushort> ByName => GameDataCatalog.JewelVisuals;

    public static ushort? Of(string name) =>
        ByName.TryGetValue(name, out ushort visual) && visual != 0 ? visual : null;
}
