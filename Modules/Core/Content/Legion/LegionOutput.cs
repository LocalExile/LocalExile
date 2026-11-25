using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Legion;

/// <summary>
/// How Legion's world records reach the clients. Every Legion object is the area's: the monolith, its pulse, the army, the
/// break-out flashes and the chests. In the area's frame (messages, ticks and timers run there) a world record sent through
/// one player goes to every client in the area (Program.SendRecords puts it in AreaDelta), and the area gives a client that
/// joins later each object's create and latest world records (AreaObjectRecords.Join; monsters from MonsterOrigins).
/// Outside the frame the record goes to that player alone. <see cref="ToArea"/> sends a record once either way.
/// </summary>
static class LegionOutput
{
    /// <summary>The players in the encounter's area, the given one first.</summary>
    public static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player).Where(p => !p.Closed && !p.LeavingArea && p.Instance == player.Instance).Distinct();

    // Game.InAreaFrame: the test Program.SendRecords makes for sending into the area's frame.
    static bool Shared(Player p) => p.Area.Owned && p.Connection.WorldOutput.Started;

    /// <summary>Calls <paramref name="send"/> so that every client in the area gets the record once: through one player whose
    /// record is the area's, then through each player whose record is its own (outside the frame, or a connection whose
    /// frames have not started). False when no player was there to send it through.</summary>
    public static Task<bool> ToArea(Player player, Func<Player, Task> send) => ToArea(Members(player).ToList(), send);

    /// <inheritdoc cref="ToArea(Player, Func{Player, Task})"/>
    public static async Task<bool> ToArea(IReadOnlyList<Player> members, Func<Player, Task> send)
    {
        // The frame's copy first: the area then records a direct send after it as that player's (AreaObjectRecords.RecordDirect)
        // and does not give it the frame's create again when its frames start; in the other order the create clears that.
        if (members.FirstOrDefault(Shared) is { } through) await send(through);
        foreach (var p in members)
            if (!Shared(p)) await send(p);
        return members.Count > 0;
    }

    /// <summary>Removes an area object from every client. When no client got the removal (nobody was in the area), the area
    /// forgets the object and its component state itself, or it would give them to every player who enters later.</summary>
    public static async Task Remove(Player player, uint id)
    {
        bool sent = await ToArea(player, p => global::Game.SendWorldRemoval(p, id));
        if (!sent || player.Area.Objects.Has(id))
        {
            player.Area.Objects.RecordDirect(id, InstanceProtocol.RemoveObject(id), AreaDelta.Kind.Remove, player);
            player.Area.Buffs.Removed(id);
            player.Area.Stats.Removed(id);
            player.Area.Life.Removed(id);
        }
    }

    /// <summary>Calls <paramref name="send"/> for <paramref name="player"/> alone (one who just entered): Program.ViewOf keeps
    /// its records from the other clients and from what the area gives later joiners.</summary>
    public static async Task ToOne(Player player, Func<Task> send)
    {
        var previous = Program.ViewOf.Value;
        Program.ViewOf.Value = player;
        try { await send(); }
        finally { Program.ViewOf.Value = previous; }
    }
}
