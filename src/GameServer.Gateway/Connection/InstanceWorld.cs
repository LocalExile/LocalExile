/// <summary>
/// An area instance's own world: its area (GameArea: its monsters), its clock and its object ids, the same for every
/// connection in it. No monster belongs to a player: whoever comes, goes or comes back, the area keeps them as they are.
/// </summary>
public sealed class InstanceWorld
{
    /// <summary>Module-owned static scene snapshots that survive rule reloads, keyed by their owner and placement.</summary>
    public readonly Dictionary<string, (uint Id, string Type, GameServer.Core.Protocol.Instance.QuestObject Object)> SceneObjects = new(StringComparer.Ordinal);
    /// <summary>The instance's area: its monsters and everything its frame owns. Seeded by the first player who comes.</summary>
    public GameArea Area => Frames.Area ?? Frames.World();

    // Numbered boss-room points survive host handoff and a return to a kept instance.
    public readonly Dictionary<string, (int X, int Y)> LaterFormPoints = new(StringComparer.Ordinal);


    /// <summary>Monsters of the area placed far from every player: the instance's, counted and saved with it, but not
    /// created yet, so they are on no client and nothing acts on them. One is created, as a kept monster is, when a
    /// player comes near. In the area's frame.</summary>
    public readonly List<SavedMonster> HeldBackMonsters = [];


    /// <summary>The instance's game time: walks, cooldowns and timers of everyone in it. Pausing stops it, so a connection
    /// pauses only when it is alone here (Connection.SetPaused).</summary>
    public readonly SimulationClock Clock = SimulationClock.StartNew();

    /// <summary>The instance's frames: one driver ends every connection's frames and steps every connection's loops on the
    /// Clock's grid, while someone is here (AreaFrames).</summary>
    public AreaFrames Frames => LazyInitializer.EnsureInitialized(ref frames, () => new AreaFrames(() => Clock));
    AreaFrames? frames;

    /// <summary>While the area steps its walkers (one pass of the area's movement, in the area's frame): what stands in
    /// each walker's way in that pass, so a walk that searches again mid-pass sees the same bodies. Null between passes.</summary>
    public Func<uint, IReadOnlyList<GameServer.Core.World.ClientPathfinder.Blocker>>? AreaBodies;

    /// <summary>The connection whose Escape menu stopped the Clock (Connection.SetPaused), until its pause ends. Under the
    /// Monsters lock.</summary>
    public Player? PausedBy;

    /// <summary>Server objects of the instance (effects, projectiles, stashes, minions): ids unique to all its clients, from
    /// 0x04000000. A client numbers the objects it makes itself (its predicted projectiles: a wand's bolt) from 0x3000:
    /// the server's from there too met them once its monsters took ids of their own (0x08000000 on) and its count stayed
    /// low, so a server bolt or explosion came with an id the client's own bolt had, entered its wake zone a second time
    /// ("ObjectInWakeZone was called on an object 12290 (Metadata/Projectiles/WandProjectile) that was already in the
    /// wake zone"), broke its object list and crashed it (live 2026-11-05). Above the player (0x1000), the lockstep tick
    /// order (ascending ids: LivingCollision.StepFrames) is kept.</summary>
    public uint NextObjectId = 0x04000000;
    public uint TakeObjectId() => Interlocked.Increment(ref NextObjectId) - 1;


    // The instance's monsters have ids of their own, the same on every client, below 0x40000000: the client itself puts
    // an object of 0x40000000 or above in its wake zone, and the server's 0x1a9 then found it there ("ObjectInWakeZone ...
    // already in the wake zone"), which broke its object list and crashed it (live 2026-11-04).
    uint _nextMonsterId = 0x08000000;
    public uint NewMonsterId() => Interlocked.Increment(ref _nextMonsterId);

    /// <summary>How each monster was created: what a client that comes later needs to create it too.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SharedMonsterOrigin> MonsterOrigins = new();

    /// <summary>The area's packs were placed (or restored): a later connection places none.</summary>
    public bool Populated;

    /// <summary>The connection the monsters' walks were last built for: the area's view (AreaFrames.View), whose loops step
    /// them in the area's frame; before the frames start, the first one in.</summary>
    public Player? Runner;
}

/// <summary>Monsters lying in wait for a player to come near, besides the buried ones (DormantMonsters), and whether a
/// connection's emerge watch looks for them. In the area's frame. The instance's, so they outlast the player who placed
/// them and a reload of the game's rules: the new rules' watch raises them.</summary>
public sealed class EmergeGround
{
    /// <summary>Lurking monsters by object id: in the instance's monsters, busy until they rise.</summary>
    public readonly HashSet<uint> Lurking = [];
    /// <summary>Whether a connection's watch runs for them.</summary>
    public bool Watching;
}
