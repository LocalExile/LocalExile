using GameServer.Core.Protocol.Instance;
using DamageType = GameServer.Core.World.DamageType;

public static partial class Game
{
    /// <summary>The monsters the instance keeps when the connection leaves (#12): all but the player's minions. Under the
    /// Monsters lock.</summary>
    public static IEnumerable<SpawnedMonster> InstanceMonsters(Player player) =>
        player.Area.Monsters.Where(kv => player.Instance.World.MonsterOrigins.ContainsKey(kv.Key)).Select(kv => kv.Value);

    /// <summary>A minion leaves no corpse: queue its body's removal (0x2bd) a second after death.
    /// Retain its identity through both server removal and publication. In the area's frame; every minion tick calls it.</summary>
    public static void RemoveMinionBodiesLocked(Player player, double now)
    {
        foreach (var body in player.Minions.Bodies.Where(b => now >= b.At).ToArray())
        {
            player.Minions.Bodies.Remove(body);
            if (!player.Area.Monsters.TryGetValue(body.Id, out var dead) || !ReferenceEquals(dead, body.Monster) || !dead.Dead) continue;
            player.Area.Monsters.Remove(body.Id);
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(body.Id), "minion body removed", body.Id, dead, PlayerMinions.OutputKind.Remove, WorldId: body.Id, RemovesObject: true));
        }
    }

    /// <summary>At disconnect: the instance keeps its monsters as they are now, for the next connection (#12); the
    /// player's minions go with him.</summary>
    public static void KeepInstanceMonsters(Player left)
    {
        SaveSummons(left);
        // The held-back ones too: they are the instance's, wherever the players went.
        if (left.MonstersPlaced)
            left.Instance.Monsters = [.. SavedMonster.Save(InstanceMonsters(left),
                AreaTime(left), MonsterEmerge.Unrisen(left)), .. HeldBackLocked(left)];
        RemoveLeavingMinionsLocked(left);
    }
}
