/// <summary>
/// Izaro's Summoning Portals (LabyrinthSecrets <c>Portals1</c>, room boss_portals.arm). In that arena three portals
/// (LabyrinthSecretEffects <c>PortalSummon1_&lt;element&gt;</c>: Thresholds of Anger, Hatred and Wrath) rise one at a time on
/// the small elevators, the first with Izaro (IzaroPortal.ot: Transitionable 1 lowered, 2 raising, 3 raised, 4 lowering,
/// 5 lowered, the state a new one is in), and bring Guardians of the Goddess of their element until destroyed. Every
/// portal not destroyed when the trial ends (risen or not) comes back in the run's later trials whose room has the
/// <c>Portals2</c> secret: <c>PortalSummon2_*</c> (IzaroPortal2.ot: untargetable, cannot be damaged; 1 open, 3 closes), in an
/// alcove whose railing lowers as he fights and on a ledge of the room before the arena.
/// The original (live, the user): what rises can be hit only once it is up; the guardians it brought stay when the trial
/// ends but ignore the player. The skills' cast and cooldown are the data's (2.17 s; 6 s, the returning ones' 3 s); the
/// data's one guardian at a time and no cap are not the original's, so <see cref="SummonCount"/> and
/// <see cref="SummonLimit"/> are the user's. The rise interval is ours, as the statues'.
/// </summary>
static class IzaroPortals
{
    public const string Mechanic = "Portals";

    /// <summary>IzaroPortal.ot's Transitionable.</summary>
    public const byte Raising = 2, Raised = 3, Lowering = 4, Lowered = 5;

    /// <summary>IzaroPortal2.ot's Transitionable: a new one is made in 3, whose create event plays the closing clip.</summary>
    public const byte ReturnedOpen = 1, ReturnedClosed = 3;

    /// <summary>portal_actual_killable/rig.amd: elevator_ascend (10.7 s) and elevator_descend (3.333 s), which the .ot leaves
    /// to the server; portal_killable/rig.amd: townportal, the returning one's idle.</summary>
    public const uint AscendClip = 0, DescendClip = 1, ReturnedIdleClip = 1;

    /// <summary>The time between the portals' rises (ours, as <see cref="IzaroStatues.RiseInterval"/>).</summary>
    public const double RiseInterval = IzaroStatues.RiseInterval;

    /// <summary>How many guardians a portal brings at a time, and how many the whole arena has at most (the user's numbers).</summary>
    public const int SummonCount = 4, SummonLimit = 20;

    /// <summary>His line after the trial's end line: how many portals are left.</summary>
    public static string FinishLine(int left) => $"IzaroFinishPortals{Math.Clamp(left, 0, 3)}";

    /// <summary>His line when the last of the arena's portals is destroyed.</summary>
    public const string AllDeadLine = "IzaroAllPortalsDead";
}
