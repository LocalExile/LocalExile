using GameServer.Data.Terrain.Generation;

/// <summary>
/// How a Labyrinth room's objects drive each other: the room file's links (RoomFile.Links, Agents/docs/game/
/// labyrinth-trials.md "Room links"), between the placed room objects. An object turned on (a lever pulled, a door
/// opened, gears set turning) passes it on along its links:
/// <list type="bullet">
/// <item><c>next</c> turns the target on (a lever's door and gears, a gear's next gear, a door's next door); turned off
/// (a crank coming back), it turns its <c>next</c> targets off again, but an opened door stays open.</item>
/// <item><c>counter</c> counts it on the target (Counter_Handler, Door_Counter), which turns on once all its counters
/// are on together (<c>Counter_Handler_Any</c>: any one); turned off, it no longer counts. A target once on stays on.</item>
/// <item><c>transition1</c>…<c>4</c>: what a Counter_Handler (or a Switch_Once_Multi) turns on, in that order; a crank
/// (<c>Switch_Reset*</c>) it takes is spent: it stays down.</item>
/// <item><c>disable</c>, <c>disable1</c>…<c>3</c>: the target lever can no longer be used.</item>
/// </list>
/// The links of the other kinds (rails, traps) are not followed here.
/// </summary>
public sealed class LabyrinthWiring
{
    public enum Change { On, Off, Disabled, Spent }

    const string Parts = "Metadata/Terrain/Labyrinth/Objects/Puzzle_Parts/";
    public const string CounterHandler = Parts + "Counter_Handler", AnyCounterHandler = Parts + "Counter_Handler_Any",
        CounterDoor = Parts + "Door_Counter", OnceMultiSwitch = Parts + "Switch_Once_Multi", CrankPrefix = Parts + "Switch_Reset";

    public static bool IsCrank(string type) => type.StartsWith(CrankPrefix, StringComparison.Ordinal);

    /// <summary>How long a crank stays down before it comes back up: its type's number of seconds
    /// (<c>Switch_Reset_16</c>, <c>_10</c>; our reading of the names), or null for one with none.</summary>
    public static double? CrankSeconds(string type) =>
        IsCrank(type) && type.LastIndexOf('_') is int at && double.TryParse(type[(at + 1)..], out double seconds) ? seconds : null;

    // The objects whose transitionN links fire when they turn on (Switch_Multi's and the plates' mean their states).
    static bool Fires(string type) => type is CounterHandler or AnyCounterHandler or OnceMultiSwitch;

    static bool IsDoor(string type) => type is LabyrinthTrials.Door or CounterDoor or LabyrinthTrials.OpenDoor;

    readonly IReadOnlyList<string> types;
    readonly List<PlacedLink>[] outgoing;
    readonly int[] counters;
    readonly HashSet<int>[] counted;
    readonly bool[] on, disabled, spent;

    public LabyrinthWiring(IReadOnlyList<string> types, IEnumerable<PlacedLink> links)
    {
        this.types = types;
        outgoing = [.. types.Select(_ => new List<PlacedLink>())];
        counters = new int[types.Count];
        counted = [.. types.Select(_ => new HashSet<int>())];
        (on, disabled, spent) = (new bool[types.Count], new bool[types.Count], new bool[types.Count]);
        foreach (var link in links)
        {
            if (link.From < 0 || link.From >= types.Count || link.To < 0 || link.To >= types.Count) continue;
            outgoing[link.From].Add(link);
            if (link.Name == "counter") counters[link.To]++;
        }
        foreach (var list in outgoing) list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    /// <summary>Whether the object has links this wiring follows (else the trials' own rules drive it).</summary>
    public bool Wired(int index) => outgoing[index].Any(l => Followed(l.Name)) || counters[index] > 0;

    static bool Followed(string name) => name is "next" or "counter" || name.StartsWith("transition", StringComparison.Ordinal)
        || name.StartsWith("disable", StringComparison.Ordinal);

    public bool IsOn(int index) => on[index];
    public bool IsDisabled(int index) => disabled[index];
    public bool IsSpent(int index) => spent[index];

    /// <summary>Turns the object on or off and passes it on; the changes in the order they happen (the object's own
    /// first), none when it already was so, or is a spent crank.</summary>
    public List<(int Index, Change Change)> Turn(int index, bool turnOn)
    {
        var changes = new List<(int, Change)>();
        if (turnOn) TurnOn(index, changes); else TurnOff(index, changes);
        return changes;
    }

    void TurnOn(int index, List<(int, Change)> changes)
    {
        if (on[index]) return;
        on[index] = true;
        changes.Add((index, Change.On));
        foreach (var link in outgoing[index])
            switch (link.Name)
            {
                case "next":
                    TurnOn(link.To, changes);
                    break;
                case "counter":
                    if (counted[link.To].Add(index) && (types[link.To] == AnyCounterHandler || counted[link.To].Count >= counters[link.To]))
                        TurnOn(link.To, changes);
                    break;
                case var name when name.StartsWith("transition", StringComparison.Ordinal) && Fires(types[index]):
                    if (IsCrank(types[link.To]))
                    {
                        if (!spent[link.To]) { spent[link.To] = true; changes.Add((link.To, Change.Spent)); }
                    }
                    else TurnOn(link.To, changes);
                    break;
                case var name when name.StartsWith("disable", StringComparison.Ordinal):
                    if (!disabled[link.To] && !on[link.To]) { disabled[link.To] = true; changes.Add((link.To, Change.Disabled)); }
                    break;
            }
    }

    void TurnOff(int index, List<(int, Change)> changes)
    {
        if (!on[index] || spent[index] || IsDoor(types[index]) || types[index] is CounterHandler or AnyCounterHandler) return;
        on[index] = false;
        changes.Add((index, Change.Off));
        foreach (var link in outgoing[index])
            if (link.Name == "next") TurnOff(link.To, changes);
            else if (link.Name == "counter") counted[link.To].Remove(index);
    }
}
