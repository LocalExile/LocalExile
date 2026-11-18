using GameServer.Modding;

namespace GameServer.Modules.Shrines;

/// <summary>What runs a shrine lifecycle step: the connection it belongs to and the connection clock's seconds.</summary>
public sealed record ShrineContext(ClientState Conn, double Now);

/// <summary>
/// One reusable piece of a shrine type's gameplay. A <see cref="ShrineDefinition"/> lists the pieces it runs while the
/// shrine is unclaimed (<see cref="ShrineDefinition.BeforeActivation"/>) and while the player's buff is active
/// (<see cref="ShrineDefinition.AfterActivation"/>); the manager calls each list's own steps, so a piece implements only
/// the half it belongs to and every step defaults to nothing.
/// <para>An effect instance belongs to one shrine: the definition lists factories, so two shrines of one type never share
/// the monsters, objects or marks an effect keeps.</para>
/// <para>An effect that creates objects owns them (through the connection's own writers) and removes them in
/// <see cref="OnExpired"/> or <see cref="OnMapUnload"/>; it treats a connection that left the area as having nothing
/// left, because the area's objects went with it.</para>
/// </summary>
public interface IShrineEffect
{
    // ---- the unclaimed shrine ----

    /// <summary>The shrine object was just created.</summary>
    Task OnSpawn(ShrineContext ctx, ShrineInstance shrine) => Task.CompletedTask;

    /// <summary>A hostile monster came within the shrine's neighbourhood.</summary>
    Task OnNearbyMonsterEnter(ShrineContext ctx, ShrineInstance shrine, uint monster) => Task.CompletedTask;

    /// <summary>A monster left the shrine's neighbourhood.</summary>
    Task OnNearbyMonsterLeave(ShrineContext ctx, ShrineInstance shrine, uint monster) => Task.CompletedTask;

    /// <summary>The player's click was accepted: the shrine is leaving its unclaimed state, so this work stops now.</summary>
    Task OnActivation(ShrineContext ctx, ShrineInstance shrine) => Task.CompletedTask;

    // ---- the player's buff ----

    /// <summary>The buff was sent and its stats are in the character's sum.</summary>
    Task OnBuffApplied(ShrineContext ctx, ShrineInstance shrine) => Task.CompletedTask;

    /// <summary>One tick while this effect runs: every tick for an unclaimed shrine's effects, and every tick of the
    /// player's buff for the ones its activation runs (an effect reads <see cref="ShrineInstance.State"/>).</summary>
    Task Tick(ShrineContext ctx, ShrineInstance shrine) => Task.CompletedTask;

    /// <summary>The buff ended by its duration: remove every object and mark this effect left.</summary>
    Task OnExpired(ShrineContext ctx, ShrineInstance shrine) => Task.CompletedTask;

    // ---- either ----

    /// <summary>The area is being unloaded: the shrine's objects and buffs are gone with it.</summary>
    Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine) => Task.CompletedTask;
}
