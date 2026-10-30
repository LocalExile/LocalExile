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
        // Lioneye's Watch: placed live with /stash_place 327 270 0 (2026-10-30).
        [AreaLayouts.LioneyesWatch] = new(327, 270, 0),
        // The Azurite Mine Encampment (Delve_Hub): placed live with /stash_place 355 295 180 (2026-10-30). It also gets
        // the guild stash (Program.SpawnTownStash), at TownGuildStashes.
        [DelveHub] = new(355, 295, MathF.PI),
    };

    /// <summary>A town's guild stash where it has a place of its own (else next to the stash), placed live with
    /// /guild_stash_place (2026-10-30): the Mine Encampment 344 295 180, Lioneye's Watch 317 270 0.</summary>
    public static Placement? GuildIn(ushort areaHash) => areaHash switch
    {
        DelveHub => new(344, 295, MathF.PI),
        AreaLayouts.LioneyesWatch => new(317, 270, 0),
        _ => null,
    };

    /// <summary>HASH16 of the Azurite Mine Encampment (WorldAreas Delve_Hub).</summary>
    public const ushort DelveHub = 0x8b9b;

    public static Placement? In(ushort areaHash) =>
        Towns.GetValueOrDefault(areaHash)
        ?? (HideoutCatalog.ByArea(areaHash)?.First(GameServer.Core.Protocol.Instance.StashObject.MetadataPath) is { } d ? new(d.X, d.Y, d.Rotation) : null);
}
