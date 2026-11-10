/// <summary>
/// Where a melee area attack lands: on the aimed point when it is within reach (the area's radius plus the weapon's
/// range), in front of the player otherwise, except for the actions that swing around the player wherever they are
/// aimed. Holy Sweep (ActionTypes <c>Sweep</c>) and Holy Sweep of Hammerfalls (<c>SweepAltX</c>) swing in a circle
/// around the player, which is where the client draws them; centred on the target, the server's circle sat about 15
/// cells towards it (live 2026-11-08: player (507, 645), target (520, 637), radius 40), missing monsters behind the
/// player and hitting some past the drawn edge.
/// </summary>
public static class MeleeAreaCentre
{
    /// <summary>ActionTypes HASH16 of the actions centred on the player: Sweep (row 0) and SweepAltX (row 1334).</summary>
    static readonly HashSet<ushort> AroundCaster = [0x8f31, 0xa0b6];

    public static (int X, int Y) Of(ushort? action, (int X, int Y) from, (int X, int Y) to, double reach) =>
        action is ushort a && AroundCaster.Contains(a) || Math.Sqrt((double)(to.X - from.X) * (to.X - from.X)
            + (double)(to.Y - from.Y) * (to.Y - from.Y)) > reach ? from : to;
}
