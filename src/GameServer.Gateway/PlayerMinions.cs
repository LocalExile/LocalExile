/// <summary>
/// The monsters the player owns (minions), the Spirit chieftains first (SpiritChieftains.cs). A minion is a monster on his
/// team (Positioned team 0) that the client knows as his by its own stats is_player_minion and is_semantic_minion. It
/// follows him (the monster AI walks friendly monsters into the player's surround and never lets an owned one give up), may
/// fight his enemies (Program.MinionAttacks), lives until its duration ends (no monster attacks a minion), and dies with
/// him. It is never kept by the instance and goes with the connection (area change, logout). Guarded by the connection's
/// Monsters lock.
/// </summary>
sealed class PlayerMinions
{
    /// <summary>One minion: its object id and monster, the skill that made it (an effect id), its kind and until when it
    /// lives (game seconds).</summary>
    internal sealed class Minion(uint id, SpawnedMonster monster, string skill, string kind)
    {
        public uint Id => id;
        public SpawnedMonster Monster => monster;
        public string Skill => skill;
        public string Kind => kind;
        public double Until;
        /// <summary>It attacks the player's enemies near it (Program.MinionAttacks).</summary>
        public bool Fights { get; init; }
    }

    readonly Dictionary<uint, Minion> minions = [];

    public IReadOnlyCollection<Minion> All => minions.Values;
    public bool Owns(uint id) => minions.ContainsKey(id);
    public bool Fights(uint id) => minions.TryGetValue(id, out var m) && m.Fights;
    public void Add(Minion minion) => minions[minion.Id] = minion;
    public void Remove(uint id) => minions.Remove(id);

    /// <summary>Ended minions' bodies still in the area and when they go.</summary>
    public readonly List<(uint Id, double At)> Bodies = [];
}

sealed partial class ClientState
{
    /// <summary>The player's minions (PlayerMinions), under the Monsters lock.</summary>
    public readonly PlayerMinions Minions = new();
}

partial class Program
{
    /// <summary>The monsters the instance keeps when the connection leaves (#12): all but the player's minions. Under the
    /// Monsters lock.</summary>
    internal static IEnumerable<SpawnedMonster> InstanceMonsters(ClientState conn) =>
        conn.Monsters.Where(kv => !conn.Minions.Owns(kv.Key)).Select(kv => kv.Value);
}
