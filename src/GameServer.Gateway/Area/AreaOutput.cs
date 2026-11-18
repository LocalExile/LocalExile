/// <summary>
/// What an area's frame shows its players: records per observer (a player entity), written by the systems (replication
/// decides who sees what) and handed to each player's session when the frame ends, before its frame end (S→C 0x2b7). No
/// system touches a socket.
/// </summary>
public sealed class AreaOutput
{
    public readonly record struct Record(byte[] Bytes, string? Log);

    readonly Dictionary<EntityId, List<Record>> byObserver = [];

    public void To(EntityId observer, byte[] bytes, string? log = null)
    {
        if (!byObserver.TryGetValue(observer, out var list)) byObserver[observer] = list = [];
        list.Add(new(bytes, log));
    }

    /// <summary>To every player of the area.</summary>
    public void ToAll(GameArea area, byte[] bytes, string? log = null)
    {
        foreach (var player in area.Entities.Ids(EntityKind.Player)) To(player, bytes, log);
    }

    /// <summary>The observer's records of this frame, in the order written; they are handed over once.</summary>
    public IReadOnlyList<Record> Take(EntityId observer) =>
        byObserver.Remove(observer, out var list) ? list : [];

    public int Count => byObserver.Values.Sum(l => l.Count);
}
