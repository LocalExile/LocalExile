using GameServer.Core.Protocol.Instance;
using DamageType = GameServer.Core.World.DamageType;

static partial class Game
{
    /// <summary>The monsters the instance keeps when the connection leaves (#12): all but the player's minions. Under the
    /// Monsters lock.</summary>
    internal static IEnumerable<SpawnedMonster> InstanceMonsters(ClientState conn) =>
        conn.Monsters.Where(kv => !conn.Minions.Owns(kv.Key)).Select(kv => kv.Value);

    /// <summary>A minion leaves no corpse: its body goes (0x2bb, into <paramref name="bytes"/>) a second after its death,
    /// once the death has played. Under the Monsters lock; every minion tick calls it.</summary>
    internal static void RemoveMinionBodiesLocked(ClientState conn, double now, List<byte> bytes)
    {
        foreach (var body in conn.Minions.Bodies.Where(b => now >= b.At).ToArray())
        {
            conn.Minions.Bodies.Remove(body);
            if (!conn.Monsters.TryGetValue(body.Id, out var dead) || !dead.Dead) continue;
            conn.Monsters.Remove(body.Id);
            bytes.AddRange(InstanceProtocol.RemoveObject(body.Id));
        }
    }

    /// <summary>At disconnect: the instance keeps its monsters as they are now, for the next connection (#12); the
    /// player's minions go with him.</summary>
    internal static void KeepInstanceMonsters(ClientState left)
    {
        lock (left.Monsters) left.Instance.Monsters = SavedMonster.Save(InstanceMonsters(left),
            left.Clock.Elapsed.TotalSeconds, MonsterEmerge.Unrisen(left));
    }
}
