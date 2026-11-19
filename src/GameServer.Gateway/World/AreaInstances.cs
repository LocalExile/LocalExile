using GameServer.Core.Protocol.Instance;

/// <summary>
/// An area's instances and when one is made again: a town keeps one live instance per league and a character's hideout
/// one per character (<see cref="GameServer.Core.World.InstanceRegistry"/>), an empty instance is gone six minutes after
/// its last player leaves, and a map's own instance is never remade. So the instance a map was entered from is often
/// gone by the time the player comes back, and Auth refuses a transfer to it ("instance N unavailable in this league"),
/// leaving the player where they are. Both the portal back (Portals) and the death screen's "Resurrect at Checkpoint"
/// (Game) ask here first.
/// </summary>
public static class AreaInstances
{
    /// <summary>Whether the area's instance is made again on the next entry: a town (one live instance per league) or a
    /// character's hideout (one per character). A map's own instance is not.</summary>
    public static bool RecreatedOnEntry(ushort area) =>
        WaypointCatalog.Areas.Find(area) is { IsTown: true } || HideoutCatalog.ByArea(area) is not null;

    /// <summary>Asks Auth for the area's live instances and whether the instance is missing from them.</summary>
    public static async Task<bool> Gone(ClientState conn, ushort area, ulong instance) =>
        Gone(await InstanceControl.CallAsync(new ControlRequest { Op = "list", Ticket = conn.Ticket, Area = area }), instance);

    /// <summary>Whether the instance list, when it answered, no longer has the instance (a transfer to it would be
    /// refused).</summary>
    public static bool Gone(ControlResponse listed, ulong instance) =>
        listed.Ok && listed.Instances is { } available && available.All(i => i.Id != instance);
}
