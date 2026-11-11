using GameServer.Core.Protocol.Instance;
using DamageType = GameServer.Core.World.DamageType;

static partial class Game
{
    /// <summary>The monsters the instance keeps when the connection leaves (#12): all but the player's minions. Under the
    /// Monsters lock.</summary>
    internal static IEnumerable<SpawnedMonster> InstanceMonsters(ClientState conn) =>
        conn.Monsters.Where(kv => conn.Instance.World.MonsterOrigins.ContainsKey(kv.Key)).Select(kv => kv.Value);

    /// <summary>A minion leaves no corpse: queue its body's removal (0x2bb) a second after death.
    /// Retain its identity through both server removal and publication. Under Monsters; every minion tick calls it.</summary>
    internal static void RemoveMinionBodiesLocked(ClientState conn, double now)
    {
        foreach (var body in conn.Minions.Bodies.Where(b => now >= b.At).ToArray())
        {
            conn.Minions.Bodies.Remove(body);
            if (!conn.Monsters.TryGetValue(body.Id, out var dead) || !ReferenceEquals(dead, body.Monster) || !dead.Dead) continue;
            conn.Monsters.Remove(body.Id);
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(body.Id), "minion body removed", body.Id, dead, PlayerMinions.OutputKind.Remove));
        }
    }

    /// <summary>At disconnect: the instance keeps its monsters as they are now, for the next connection (#12); the
    /// player's minions go with him.</summary>
    internal static void KeepInstanceMonsters(ClientState left)
    {
        SaveSummons(left);
        lock (left.Monsters)
        {
            if (left.MonstersPlaced)
                left.Instance.Monsters = SavedMonster.Save(InstanceMonsters(left),
                    left.Clock.Elapsed.TotalSeconds, MonsterEmerge.Unrisen(left));
            RemoveLeavingMinionsLocked(left);
        }
    }
}
