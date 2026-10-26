/// <summary>
/// Where a town's Stash stands (Agents/docs/protocol/18-stash.md §1). The client files do not place it (Stash.ot has only
/// its model; the town tiles' .tmd records hold the waypoint and doodads), so the cells are ours, as for the NPCs. A
/// hideout's comes from its decoration (HideoutCatalog).
/// </summary>
static class StashCatalog
{
    public sealed record Placement(int X, int Y, float Rotation);

    static readonly Dictionary<ushort, Placement> Towns = new()
    {
        // Lioneye's Watch: next to Nessa (346, 270), the place the user picked (2026-10-19).
        [AreaLayouts.LioneyesWatch] = new(319, 272, 0),
        // The Azurite Mine Encampment (Delve_Hub): across the waypoint (333, 402) from Niko (342, 410); our own cell
        // (2026-10-21). It also gets the guild stash (Program.SpawnTownStash).
        [DelveHub] = new(322, 412, 0),
    };

    /// <summary>HASH16 of the Azurite Mine Encampment (WorldAreas Delve_Hub).</summary>
    public const ushort DelveHub = 0x8b9b;

    public static Placement? In(ushort areaHash) =>
        Towns.GetValueOrDefault(areaHash)
        ?? (HideoutCatalog.ByArea(areaHash)?.First(GameServer.Core.Protocol.Instance.StashObject.MetadataPath) is { } d ? new(d.X, d.Y, d.Rotation) : null);
}
