using static GameServer.Modules.Betrayal.BetrayalTables;

namespace GameServer.Modules.Betrayal;

/// <summary>
/// One instance's Betrayal mission, as plain state: its plan, the objects it put in the area, how far it has gone and what
/// is due when. It belongs to the area instance, so a second party member, a portal re-entry and a module reload all read
/// the same one. Its clock is the frames the area has run (<see cref="Step"/>), not real time.
/// </summary>
public sealed class BetrayalRun(BetrayalPlan.Plan plan, byte level)
{
    /// <summary>Waiting: Jun and the mission stand, nobody came close. Fight: the member is out. Defeated: it kneels by its
    /// MakeChoice. Chosen: the choice is made and the member is about to leave. Done: it has left.</summary>
    public enum Stage { Waiting, Fight, Defeated, Chosen, Done }

    /// <summary>What a timer does when it is due.</summary>
    public enum Due { RemovePortal, RemoveSpawners, Shackles, Interrogate, Leave }

    /// <summary>One frame at 30 a second.</summary>
    public const double FrameSeconds = 1.0 / 30;

    /// <summary>Frames from each step to what follows it [capture 1/1 each]: the entrance portal lives 71; the portals are
    /// removed 31 after they close; a non-Transportation member's shackles go 1 to 2 52 after the kneel; after the choice the
    /// member's interrogate state is set at 46 and it leaves with Jun and the carts at 47.</summary>
    public const int PortalFrames = 71, SpawnersFrames = 31, ShacklesFrames = 52, InterrogateFrames = 46, LeaveFrames = 47;

    readonly object gate = new();
    readonly List<(double At, Due What)> timers = [];
    Stage stage;
    double clock;

    public BetrayalPlan.Plan Plan { get; } = plan;
    public byte Level { get; } = level;

    // The objects the mission put in the area. Set by the one caller that made them.
    public uint Jun { get; set; }
    public List<uint> Carts { get; } = [];
    public List<uint> Spawners { get; } = [];
    public uint Portal { get; set; }
    public uint Member { get; set; }
    public object? MemberBody { get; set; }
    public uint Choice { get; set; }

    /// <summary>The cell the MakeChoice was created on (set at the kneel): the cell a character walks to to use it.</summary>
    public (int X, int Y)? ChoiceCell { get; set; }

    public Stage Current { get { lock (gate) return stage; } }

    /// <summary>The Intervention's ruleset data (0x2c9 of JobAssassinationRules): 1 while the member fights, else 0 [capture:
    /// 0 at the area's entry and at the defeat, 1 at the start]. Null for a Transportation, which has no ruleset, and once the
    /// mission is done.</summary>
    public byte? RulesetPayload
    {
        get { lock (gate) return Plan.Job != Job.Intervention || stage == Stage.Done ? null : stage == Stage.Fight ? (byte)1 : (byte)0; }
    }

    /// <summary>Moves the mission from <paramref name="from"/> to the next stage: true for the one caller that does it.</summary>
    public bool Advance(Stage from)
    {
        lock (gate)
        {
            if (stage != from || stage == Stage.Done) return false;
            stage = from + 1;
            return true;
        }
    }

    /// <summary>Puts <paramref name="what"/> <paramref name="frames"/> frames from now.</summary>
    public void After(int frames, Due what)
    {
        lock (gate) timers.Add((clock + frames * FrameSeconds, what));
    }

    /// <summary>The area ran <paramref name="seconds"/>: what is due now, the earliest first.</summary>
    public IReadOnlyList<Due> Step(double seconds)
    {
        lock (gate)
        {
            clock += seconds;
            var due = timers.Where(t => clock + 1e-9 >= t.At).OrderBy(t => t.At).Select(t => t.What).ToList();
            timers.RemoveAll(t => clock + 1e-9 >= t.At);
            return due;
        }
    }
}
