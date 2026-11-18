/// <summary>
/// What an area's frame shows its players: records per observer (a player entity), written by the systems (replication
/// decides who sees what) and handed to each player's session right after the area's step, so they open the frame. A
/// record about an object names it (<see cref="Record.Object"/>): a lockstep client gets it once it has the object. No
/// system touches a socket.
/// </summary>
public sealed class AreaOutput
{
    public readonly record struct Record(byte[] Bytes, string? Log, uint? Object = null);

    readonly Dictionary<EntityId, List<Record>> byObserver = [];

    public void To(EntityId observer, byte[] bytes, string? log = null, uint? about = null)
    {
        if (!byObserver.TryGetValue(observer, out var list)) byObserver[observer] = list = [];
        list.Add(new(bytes, log, about));
    }

    /// <summary>To every player of the area.</summary>
    public void ToAll(GameArea area, byte[] bytes, string? log = null, uint? about = null)
    {
        foreach (var player in area.Entities.Ids(EntityKind.Player)) To(player, bytes, log, about);
    }

    /// <summary>Forgets what was not handed over (an observer that left).</summary>
    public void Clear() => byObserver.Clear();

    /// <summary>The observer's records of this frame, in the order written; they are handed over once.</summary>
    public IReadOnlyList<Record> Take(EntityId observer) =>
        byObserver.Remove(observer, out var list) ? list : [];

    public int Count => byObserver.Values.Sum(l => l.Count);
}
