/// <summary>
/// Izaro's Summoning Portals (LabyrinthSecrets <c>Portals1</c>, room boss_portals.arm). In that arena three portals
/// (LabyrinthSecretEffects <c>PortalSummon1_&lt;element&gt;</c>: Thresholds of Anger, Hatred and Wrath) rise one at a time on
/// the small elevators, the first with Izaro (IzaroPortal.ot: Transitionable 1 lowered, 2 raising, 3 raised, 5 lowered, the
/// state a new one is in), and bring Guardians of the Goddess of their element until destroyed. Every portal not
/// destroyed when the trial ends (risen or not) comes back in the run's later trials whose room has the <c>Portals2</c>
/// secret: <c>PortalSummon2_*</c> (IzaroPortal2.ot: untargetable, cannot be damaged; 1 open), in an alcove whose railing
/// lowers as he fights and on a ledge of the room before the arena.
/// The original (live, the user): what rises can be hit only once it is up; each portal brings one guardian about every
/// 5 s, 10 of its own at most, which comes out of it, walks a little, then fights; when he leaves the portals stay up and bring no more, and the guardians
/// fight on. The returning ones' interval is the skill's cooldown; the rise interval is ours, as the statues'.
/// </summary>
public static class IzaroPortals
{
    public const string Mechanic = "Portals";

    /// <summary>IzaroPortal.ot's Transitionable.</summary>
    public const byte Raising = 2, Raised = 3;

    /// <summary>IzaroPortal2.ot's Transitionable: a new one is made in 3, whose create event plays the closing clip.</summary>
    public const byte ReturnedOpen = 1;

    /// <summary>portal_actual_killable/rig.amd: elevator_ascend (10.7 s), which the .ot leaves to the server;
    /// portal_killable/rig.amd: townportal, the returning one's idle.</summary>
    public const uint AscendClip = 0, ReturnedIdleClip = 1;

    /// <summary>The time between the portals' rises (ours, as <see cref="IzaroStatues.RiseInterval"/>).</summary>
    public const double RiseInterval = IzaroStatues.RiseInterval;

    /// <summary>How often a portal brings a guardian, and how many of its own live at most (the original, live).</summary>
    public const double SummonSeconds = 5;
    public const int SummonLimit = 10;

    /// <summary>How far to either side of the portal's way a guardian's walk out may end (ours).</summary>
    public const int ExitSpread = 4;

    /// <summary>The guardians a portal brings. Their skills cast within <c>spell_maximum_action_distance_+%</c> -60 of the
    /// variety's attack distance, so they walk up (the original, live), unlike a ranged spell's aggro range.</summary>
    public static bool IsGuardian(MonsterCatalog.Variety v) => v.Id.Contains("IzaroSummonSkeletonCaster", StringComparison.Ordinal);
    public const double GuardianReach = 0.4;

    /// <summary>His line after the trial's end line: how many portals are left.</summary>
    public static string FinishLine(int left) => $"IzaroFinishPortals{Math.Clamp(left, 0, 3)}";

    /// <summary>His line when the last of the arena's portals is destroyed.</summary>
    public const string AllDeadLine = "IzaroAllPortalsDead";
}
