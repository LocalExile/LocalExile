/// <summary>
/// Where a town's Stash stands (Agents/docs/protocol/18-stash.md §1). In most towns the client files do not place it
/// (Stash.ot has only its model; the town tiles' .tmd records hold the waypoint and doodads), so the cells are ours, as
/// for the NPCs. The three rooms that do name it as a server object line -- Oriath Docks, Kingsmarch and the Monastery
/// of the Keepers -- stand it at the room's own cell instead, through the room placement in TownNpcs. A hideout's comes
/// from its decoration (HideoutCatalog).
/// </summary>
public static class StashCatalog
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
        // Act 6 Lioneye's Watch: positions confirmed in game by the user.
        [LioneyesWatchAct6] = new(329, 408, 0),
        // The Menagerie (Menagerie_Hub): placed live with /stash_place 455 480 0 (2026-10-30).
        [MenagerieHub] = new(455, 480, 0),
        // Oriath Docks (2_10_town): dock_town.arm's own object line, at its room cell. The room's placement code is 0 with
        // its origin at (0, 0) (TownNpcs), so a room cell is an area cell: the stash (532, 296), the guild stash (542,
        // 276) and the crafting bench (507, 300) stand as the client's room puts them, left to right on screen.
        [OriathDocks] = new(532, 296, -0.800368f),
        // The Forbidden Sanctum (SanctumFoyer_Fellshrine): by the waypoint (126, 149), to be placed live with /stash_place.
        [SanctumHub] = new(116, 160, 0),
        // Kingsmarch: kingsmarch.arm's own object line, at its room cell. The room is placed with code 0 at (138, 138)
        // (TownNpcs), so its line (420, 611) -1.5708 is the cell (558, 749) and the angle 2 * MathF.PI - 1.5708 = 4.71239,
        // by the tavern with the guild stash. No capture of ours enters Kingsmarch, so the room file is the only source.
        // The room has a second Stash line, (214, 533) -1.5708 = the cell (352, 671) by Isla and the recombinator; the
        // server creates one stash per town (Stash.SpawnTownStash), so the pair by the tavern is the one placed.
        [Kingsmarch] = new(558, 749, 2 * MathF.PI - 1.5708f),
        // The Rogue Harbour: hub_1.arm's own object line (174, 298, -1.07947), placed at (92, 92) with code 0 (TownNpcs), so
        // the cell is (266, 390) and the angle 2 pi - 1.07947. The official server creates its stash at that cell and angle.
        [HeistHub] = new(266, 390, 2 * MathF.PI - 1.07947f),
        // The Monastery of the Keepers: hub.arm's own object line, at its room cell. The room is placed with code 0 at
        // (0, 0) (TownNpcs), so the line (341, 202) 1.95822 is the cell itself -- which is what the official capture
        // creates there, byte for byte (an official capture of the Monastery: the stash's body `55010000 ca000000
        // f4a6fa3f`, the guild stash's `5e010000 ba000000 ec690f40`).
        [ChayulaHub] = new(341, 202, 1.95822f),
        // The Sovereign (DeepwaterHub): hub.arm's own object line (306, 218, -0.261801), placed at (0, 276) with code 0 by
        // hub.dgr on every seed, so area cell = room cell + (0, 276). The guild stash is its line (293, 219, 0.0799581).
        [DeepwaterHub] = new(306, 494, -0.261801f),
    };

    /// <summary>A town's guild stash where it has a place of its own (else next to the stash), placed live with
    /// /guild_stash_place (2026-10-30): the Mine Encampment 344 295 180, Lioneye's Watch 317 270 0, the Menagerie 440 480 0;
    /// the Sarn Encampment 285 418 28 (2026-11-04).</summary>
    public static Placement? GuildIn(ushort areaHash) => areaHash switch
    {
        DelveHub => new(344, 295, MathF.PI),
        MenagerieHub => new(440, 480, 0),
        SanctumHub => new(106, 166, 0),
        // Kingsmarch: kingsmarch.arm's guild stash line (420, 641) -1.5708, the room cell (558, 779).
        Kingsmarch => new(558, 779, 2 * MathF.PI - 1.5708f),
        // The Rogue Harbour: hub_1.arm's guild stash line (174, 287, -1.56009), the cell (266, 379).
        HeistHub => new(266, 379, 2 * MathF.PI - 1.56009f),
        // The Monastery of the Keepers: hub.arm's guild stash line (350, 186) 2.24084, the room cell itself.
        ChayulaHub => new(350, 186, 2.24084f),
        AreaLayouts.LioneyesWatch => new(317, 270, 0),
        OriathDocks => new(542, 276, -MathF.PI),
        SarnEncampment => new(285, 418, 28 * MathF.PI / 180),
        LioneyesWatchAct6 => new(318, 408, 0),
        DeepwaterHub => new(293, 495, 0.0799581f),
        _ => null,
    };

    /// <summary>HASH16 of Act 6 Lioneye's Watch (WorldAreas 2_6_town).</summary>
    public const ushort LioneyesWatchAct6 = 0xb0fb;

    /// <summary>HASH16 of Oriath Docks (WorldAreas 2_10_town).</summary>
    public const ushort OriathDocks = 0x95b0;

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

    /// <summary>Areas whose stash, guild stash and NPC places are offsets from the arrival cell: not placed live yet.
    /// No town is on it now: Kingsmarch, the Rogue Harbour and the Monastery of the Keepers are placed from their
    /// rooms' lines (TownNpcs).</summary>
    public static bool FromArrival(ushort areaHash) => false;

    /// <summary>HASH16 of the Monastery of the Keepers (WorldAreas ChayulaLeague).</summary>
    public const ushort ChayulaHub = 0xcb44;

    /// <summary>HASH16 of The Sovereign (WorldAreas DeepwaterHub).</summary>
    public const ushort DeepwaterHub = 0x5844;

    /// <summary>HASH16 of the Rogue Harbour (WorldAreas HeistHub).</summary>
    public const ushort HeistHub = 0xb5be;

    public static Placement? In(ushort areaHash) =>
        Towns.GetValueOrDefault(areaHash)
        ?? (HideoutCatalog.ByArea(areaHash)?.First(GameServer.Core.Protocol.Instance.StashObject.MetadataPath) is { } d ? new(d.X, d.Y, d.Rotation) : null);
}
