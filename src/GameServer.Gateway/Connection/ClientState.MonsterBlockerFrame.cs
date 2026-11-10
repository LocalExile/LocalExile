using System.Diagnostics;
using GameServer.Core.World;

public sealed partial class ClientState
{
    MonsterBlockerFrame? monsterFrame;

    /// <summary>One lock-owned position/index snapshot shared by all path orders in an AI or correction pass.</summary>
    public MonsterBlockerFrame BeginMonsterBlockerFrame(double now)
    {
        if (!Monitor.IsEntered(Monsters)) throw new SynchronizationLockException("A blocker frame requires the combat lock.");
        var frame = new MonsterBlockerFrame(this, now, monsterFrame);
        monsterFrame = frame;
        return frame;
    }

    public sealed class MonsterBlockerFrame : IDisposable
    {
        readonly ClientState owner;
        readonly MonsterBlockerFrame? outer;
        readonly BlockerSet current;
        readonly Dictionary<uint, (int Body, int Current)> indices = [];
        readonly Dictionary<uint, IReadOnlyList<ClientPathfinder.Blocker>> views = [];
        readonly int playerBody = -1;
        readonly IReadOnlyList<ClientPathfinder.Blocker> forPlayer;
        public readonly List<(uint Id, int X, int Y, int Size)> Bodies = [];
        public IReadOnlyList<ClientPathfinder.Blocker> FixedBlockers { get; }

        public MonsterBlockerFrame(ClientState owner, double now, MonsterBlockerFrame? outer, bool placement = false)
        {
            long startedAt = Stopwatch.GetTimestamp();
            this.owner = owner; this.outer = outer;
            var fixedBodies = new List<ClientPathfinder.Blocker>(owner.StationaryBlockers);
            foreach (var peer in owner.Instance?.PlayersExcept(owner.PeerId) ?? [])
                if (peer.Life > 0 && peer.Owner is not null) fixedBodies.Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
            FixedBlockers = new BlockerSet(fixedBodies);
            current = new(fixedBodies);
            if (owner.PlayerSent && owner.PlayerLife > 0 && !owner.IsPhasing)
            {
                var player = owner.Walk.Position(now);
                playerBody = current.Add(new(player.X, player.Y, LivingCollision.PlayerSize));
            }
            // Runtime collision follows native bodies. Proposed spawning deliberately retains its existing
            // clearance policy; it is not evidence of GGG's authoritative spawn-placement algorithm.
            IEnumerable<KeyValuePair<uint, SpawnedMonster>> actors = placement ? owner.Monsters : owner.CollisionMonsters;
            foreach (var (id, m) in actors)
            {
                if (placement && !LivingCollision.StepsWithBodies(m)) continue;
                var p = m.Walk.Position(now);
                int size = LivingCollision.Size(m);
                ClientPathfinder.Blocker? selected = placement ? new(p.X, p.Y, size) : owner.MonsterBody(id, m, now);
                if (selected is not { } blocker) continue;
                indices.Add(id, (Bodies.Count, current.Add(blocker)));
                Bodies.Add((id, blocker.X, blocker.Y, blocker.Size));
            }
            // Phasing (Quartz Flask): the player and the monsters pass each other.
            forPlayer = owner.IsPhasing ? FixedBlockers : playerBody >= 0 ? current.Without(playerBody) : current;
            owner.Performance.Elapsed(LatencyPhase.BlockerBuild, startedAt);
        }

        /// <summary>What stands in the player's way.</summary>
        public IReadOnlyList<ClientPathfinder.Blocker> ForPlayer => forPlayer;

        public void MovePlayer(int x, int y)
        {
            if (playerBody >= 0) current.Replace(playerBody, new(x, y, LivingCollision.PlayerSize));
        }

        public IReadOnlyList<ClientPathfinder.Blocker> For(uint self)
        {
            if (!indices.TryGetValue(self, out var index)) return current;
            if (!views.TryGetValue(self, out var view)) views[self] = view = current.Without(index.Current, -1);
            return view;
        }

        public void Move(uint id, int x, int y)
        {
            // A nonblocking actor still takes movement turns, but has no body entry to update.
            if (!indices.TryGetValue(id, out var index)) return;
            var before = Bodies[index.Body];
            current.Replace(index.Current, new(x, y, before.Size));
            Bodies[index.Body] = (id, x, y, before.Size);
        }

        public void Dispose() => owner.monsterFrame = outer;
    }
}
