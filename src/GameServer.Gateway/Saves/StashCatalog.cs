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
        // The Sarn Encampment (1_3_town): placed live with /stash_place 295 423 28 (2026-11-04).
        [SarnEncampment] = new(295, 423, 28 * MathF.PI / 180),
        // The Menagerie (Menagerie_Hub): placed live with /stash_place 455 480 0 (2026-10-30).
        [MenagerieHub] = new(455, 480, 0),
        // The Forbidden Sanctum (SanctumFoyer_Fellshrine): by the waypoint (126, 149), to be placed live with /stash_place.
        [SanctumHub] = new(116, 160, 0),
        // Kingsmarch: next to the arrival (an offset, FromArrival), to be placed live with /stash_place.
        [Kingsmarch] = new(6, 6, 0),
        // The Rogue Harbour: next to the arrival (an offset, FromArrival), to be placed live with /stash_place.
        [HeistHub] = new(6, 8, 0),
        // The Monastery of the Keepers: next to the arrival (an offset, FromArrival), to be placed live with /stash_place.
        [ChayulaHub] = new(6, 8, 0),
    };

    /// <summary>A town's guild stash where it has a place of its own (else next to the stash), placed live with
    /// /guild_stash_place (2026-10-30): the Mine Encampment 344 295 180, Lioneye's Watch 317 270 0, the Menagerie 440 480 0;
    /// the Sarn Encampment 285 418 28 (2026-11-04).</summary>
    public static Placement? GuildIn(ushort areaHash) => areaHash switch
    {
        DelveHub => new(344, 295, MathF.PI),
        MenagerieHub => new(440, 480, 0),
        SanctumHub => new(106, 166, 0),
        Kingsmarch => new(-6, 6, 0),
        HeistHub => new(-6, 8, 0),
        ChayulaHub => new(-6, 8, 0),
        AreaLayouts.LioneyesWatch => new(317, 270, 0),
        SarnEncampment => new(285, 418, 28 * MathF.PI / 180),
        _ => null,
    };

    /// <summary>HASH16 of the Sarn Encampment (WorldAreas 1_3_town).</summary>
    public const ushort SarnEncampment = 0x0431;

    /// <summary>HASH16 of the Azurite Mine Encampment (WorldAreas Delve_Hub).</summary>
    public const ushort DelveHub = 0x8b9b;

    /// <summary>HASH16 of the Menagerie (WorldAreas Menagerie_Hub).</summary>
    public const ushort MenagerieHub = 0xf3f4;

    /// <summary>HASH16 of the Forbidden Sanctum (WorldAreas SanctumFoyer_Fellshrine).</summary>
    public const ushort SanctumHub = 0xe217;

    /// <summary>HASH16 of Kingsmarch (WorldAreas KalguuranSettlersLeague).</summary>
    public const ushort Kingsmarch = 0x8f53;

    /// <summary>Areas whose stash, guild stash and NPC places are offsets from the arrival cell: not placed live yet.</summary>
    public static bool FromArrival(ushort areaHash) => areaHash is Kingsmarch or HeistHub or ChayulaHub;

    /// <summary>HASH16 of the Monastery of the Keepers (WorldAreas ChayulaLeague).</summary>
    public const ushort ChayulaHub = 0xcb44;

    /// <summary>HASH16 of the Rogue Harbour (WorldAreas HeistHub).</summary>
    public const ushort HeistHub = 0xb5be;

    public static Placement? In(ushort areaHash) =>
        Towns.GetValueOrDefault(areaHash)
        ?? (HideoutCatalog.ByArea(areaHash)?.First(GameServer.Core.Protocol.Instance.StashObject.MetadataPath) is { } d ? new(d.X, d.Y, d.Rotation) : null);
}
