/// <summary>
/// An area instance's own world: its monsters, its clock and its object ids, the same for every connection in it. No
/// monster belongs to a player: whoever comes, goes or comes back, the instance keeps them as they are. The monsters
/// dictionary is also the instance's lock (ClientState.Monsters, which every connection's code takes).
/// </summary>
public sealed class InstanceWorld
{
    /// <summary>The instance's monsters (and the players' minions in it) by object id; the instance's lock.</summary>
    public readonly SortedDictionary<uint, SpawnedMonster> Monsters = [];

    /// <summary>Created monsters waiting to emerge, under the same Monsters lock and instance lifetime.</summary>
    public readonly SortedDictionary<uint, SpawnedMonster> DormantMonsters = [];

    /// <summary>The instance's game time: walks, cooldowns and timers of everyone in it. Pausing stops it, so a connection
    /// pauses only when it is alone here (Connection.SetPaused).</summary>
    public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Server objects of the instance (effects, projectiles, stashes, minions): ids unique to all its clients, from
    /// 0x04000000. A client numbers the objects it makes itself (its predicted projectiles: a wand's bolt) from 0x3000:
    /// the server's from there too met them once its monsters took ids of their own (0x08000000 on) and its count stayed
    /// low, so a server bolt or explosion came with an id the client's own bolt had, entered its wake zone a second time
    /// ("ObjectInWakeZone was called on an object 12290 (Metadata/Projectiles/WandProjectile) that was already in the
    /// wake zone"), broke its object list and crashed it (live 2026-11-05). Above the player (0x1000), the lockstep tick
    /// order (ascending ids: LivingCollision.StepFrames) is kept.</summary>
    public uint NextObjectId = 0x04000000;

    /// <summary>Buff instances (S→C 0x1c1) of everyone in the instance (ClientState.NextBuff).</summary>
    public ushort NextBuff = 1;

    // The instance's monsters have ids of their own, the same on every client, below 0x40000000: the client itself puts
    // an object of 0x40000000 or above in its wake zone, and the server's 0x1a9 then found it there ("ObjectInWakeZone ...
    // already in the wake zone"), which broke its object list and crashed it (live 2026-11-04).
    uint _nextMonsterId = 0x08000000;
    public uint NewMonsterId() => Interlocked.Increment(ref _nextMonsterId);

    /// <summary>How each monster was created: what a client that comes later needs to create it too.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SharedMonsterOrigin> MonsterOrigins = new();

    /// <summary>The area's packs were placed (or restored): a later connection places none.</summary>
    public bool Populated;

    /// <summary>The connection whose loops step the monsters (AI, collisions, wake zone) until the instance runs them itself:
    /// the first in, then whoever is still here.</summary>
    public ClientState? Runner;
}
