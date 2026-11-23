using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The towns of the game: the WorldAreas rows with <c>IsTown</c> set (the WorldAreas client table),
/// as WorldAreas <c>Id</c> and <c>HASH16</c>. 3.29.3c has 12 act towns, Lioneye's Watch
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

    /// <summary>The town rows of the WorldAreas table, in table order; <see cref="Default"/> without the table.</summary>
    public static IReadOnlyList<(string Id, ushort Hash16)> Load(ClientTable? worldAreas)
    {
        if (worldAreas is null) return Default;
        return [.. worldAreas
            .Where(r => r.Bool("IsTown") && !Excluded.Contains(r.Text("Id") ?? ""))
            .Select(r => (r.Text("Id")!, (ushort)r.Int("HASH16")))];
    }
}
