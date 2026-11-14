using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Shrines;

/// <summary>
/// Map shrines: the level generator's shrine rooms place <c>Metadata/Shrines/Shrine</c> lines, and the manager spawns one
/// shrine object per line on walkable ground, rolls its type from the area seed, and runs its lifecycle (a click in
/// walk-up range claims it, its buff lasts the Shrines row's duration, and leaving the area cleans up).
/// <para>This module only wires the framework to the manager: the types, their buffs and their guards are data
/// (<see cref="ShrineCatalog"/>), and per-type gameplay is a behaviour or an effect in <see cref="ShrineRegistry"/>, so a
/// new shrine never changes this file.</para>
/// </summary>
public sealed class ShrinesModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        string id = module.Manifest.Id;
        ShrineRegistry.RegisterDefaults();
        GameEvents.AreaGenerated.Add(id, conn => ShrineManager.SpawnArea(conn));
        GameEvents.ObjectUse.Add(id, (conn, use, requestedAt) => ShrineManager.Use(conn, use, requestedAt));
        GameEvents.Tick.Add(id, (conn, _) => ShrineManager.Tick(conn));
        GameEvents.PlayerLeaving.Add(id, (conn, _) => ShrineManager.LeaveArea(conn));
        // The claimed shrines' buff stats join the character's own sum, so the hit, crit, armour and resistance paths
        // reading it see them; the buff's expiry takes them out again.
        module.Lifetime.Own(GameEvents.PlayerStatSources.Add(id, (conn, stats) => ShrineManager.AddStats(conn, stats)));
        // Dev: the basic types in the area on a ring, so each type's icon, buff and tooltip can be checked in one place.
        GameEvents.Command(id, "shrines",
            "/shrines [Id …] (dev: place shrine types on a ring around the player; no name places every basic type)",
            async (conn, args) =>
            {
                int made = await ShrineManager.SpawnAroundPlayer(conn, args);
                await PlayerOf(conn).Tell($"Shrines: {made} created around you");
            });
    }
}
