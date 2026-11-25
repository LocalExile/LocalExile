namespace GameServer.Modules.Bestiary;

/// <summary>
/// One instance's Bestiary mission, as plain state: the beasts it placed, how many are captured, whether Einhar has come and
/// whether the mission is complete. It belongs to the area instance, so a second party member, a portal re-entry and a
/// module reload all read the same one; what each player's client shows of it (the quest flags) follows from
/// <see cref="Received"/> and <see cref="Complete"/>.
/// </summary>
public sealed class BestiaryRun
{
    /// <summary>A mission beast: its object id, the body that a kill is matched against, and whether it is a red one.</summary>
    public sealed class Beast(uint id, object body, bool red)
    {
        public uint Id { get; } = id;
        public object Body { get; } = body;
        public bool Red { get; } = red;
        public bool Captured { get; internal set; }
    }

    /// <summary>What one capture changes: the beast, whether it is the first (Einhar arrives) and whether it is the last.</summary>
    public readonly record struct Capture(Beast Beast, bool First, bool Last);

    readonly object gate = new();
    readonly List<Beast> beasts = [];
    bool einharCame, completed;

    public string MissionId { get; init; } = "";
    public byte Level { get; init; }

    /// <summary>The mission's beasts, placed first, captured as they are killed.</summary>
    public IReadOnlyList<Beast> Beasts { get { lock (gate) return [.. beasts]; } }

    public int Total { get { lock (gate) return beasts.Count; } }
    public int Captured { get { lock (gate) return beasts.Count(b => b.Captured); } }

    /// <summary>EinharMission-Received: Einhar has arrived.</summary>
    public bool Received { get { lock (gate) return einharCame; } }

    /// <summary>EinharMission-Complete: every beast is captured.</summary>
    public bool Complete { get { lock (gate) return completed; } }

    public Beast Add(uint id, object body, bool red)
    {
        var beast = new Beast(id, body, red);
        lock (gate) beasts.Add(beast);
        return beast;
    }

    /// <summary>A kill of <paramref name="body"/>: the capture it makes, or null when it is not a mission beast still to take.</summary>
    public Capture? Kill(object body)
    {
        lock (gate)
        {
            if (beasts.FirstOrDefault(b => !b.Captured && ReferenceEquals(b.Body, body)) is not { } beast) return null;
            beast.Captured = true;
            bool first = !einharCame;
            einharCame = true;
            bool last = !completed && beasts.All(b => b.Captured);
            if (last) completed = true;
            return new(beast, first, last);
        }
    }

    /// <summary>Einhar arrives (a beast brought low): true for the one caller that makes him arrive.</summary>
    public bool TryArrive()
    {
        lock (gate)
        {
            if (einharCame) return false;
            return einharCame = true;
        }
    }
}
