using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The towns of the game: the WorldAreas rows with <c>IsTown</c> set (data/tables/WorldAreas.json, from
/// Tools/PoEDataExtractor), as WorldAreas <c>Id</c> and <c>HASH16</c>. 3.29.3c has 12 act towns, Lioneye's Watch
/// (1_1_town) to Karui Shores (2_11_endgame_town). Without the table only Lioneye's Watch is known.
/// </summary>
public static class WorldAreaTowns
{
    public static readonly IReadOnlyList<(string Id, ushort Hash16)> Default = [("1_1_town", 0x235c)];

    // pvpTown is IsTown too, but PvP is not implemented: entering it would give a town nobody can use.
    static readonly HashSet<string> Excluded = ["pvpTown"];

    /// <summary>HASH16 of <see cref="Accounts.AccountService.StartingArea"/>, The Twilight Strand (1_1_1).</summary>
    public const ushort StartingAreaHash = 0xf00a;

    /// <summary>
    /// Where a login puts a character (WorldAreas HASH16): its last town, else the starting area, The Twilight Strand,
    /// as in the real game, which logs every character in at its last town's spawn point, never in a combat area, and a
    /// character that has not reached Lioneye's Watch yet on the Strand. A last town that is not in
    /// <paramref name="towns"/> counts as none.
    /// </summary>
    public static ushort LoginArea(string? lastTown, IReadOnlyList<(string Id, ushort Hash16)> towns) =>
        lastTown is not null && towns.FirstOrDefault(t => t.Id == lastTown) is { Id: not null } town ? town.Hash16 : StartingAreaHash;

    /// <summary>The town rows of a WorldAreas.json, in table order; <see cref="Default"/> when the file is missing.</summary>
    public static IReadOnlyList<(string Id, ushort Hash16)> Load(string worldAreasJson)
    {
        if (!File.Exists(worldAreasJson)) return Default;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(worldAreasJson));
        return [.. doc.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("IsTown").GetBoolean() && !Excluded.Contains(r.GetProperty("Id").GetString() ?? ""))
            .Select(r => (r.GetProperty("Id").GetString()!, (ushort)r.GetProperty("HASH16").GetInt32()))];
    }
}
