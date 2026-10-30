using System.Diagnostics;
using GameServer.Core.World;

sealed partial class ClientState
{
    MonsterBlockerFrame? monsterFrame;

    /// <summary>One lock-owned position/index snapshot shared by all path orders in an AI or correction pass.</summary>
    public MonsterBlockerFrame BeginMonsterBlockerFrame(double now, bool includePrevious = false)
    {
        if (!Monitor.IsEntered(Monsters)) throw new SynchronizationLockException("A blocker frame requires the combat lock.");
        var frame = new MonsterBlockerFrame(this, now, includePrevious, monsterFrame);
        monsterFrame = frame;
        return frame;
    }

    public sealed class MonsterBlockerFrame : IDisposable
    {
        readonly ClientState owner;
        readonly MonsterBlockerFrame? outer;
        readonly BlockerSet current;
        readonly BlockerSet? corrections;
        readonly Dictionary<uint, (int Body, int Current, int Correction, int Previous)> indices = [];
        readonly Dictionary<uint, IReadOnlyList<ClientPathfinder.Blocker>> currentViews = [], correctionViews = [];
        public readonly List<(uint Id, int X, int Y, int Size)> Bodies = [];
        public IReadOnlyList<ClientPathfinder.Blocker> FixedBlockers { get; }
        public bool IncludesPrevious => corrections is not null;

        internal MonsterBlockerFrame(ClientState owner, double now, bool includePrevious, MonsterBlockerFrame? outer)
        {
            long startedAt = Stopwatch.GetTimestamp();
            this.owner = owner; this.outer = outer;
            var fixedBodies = new List<ClientPathfinder.Blocker>(owner.StationaryBlockers);
            foreach (var peer in owner.Instance?.PlayersExcept(owner.PeerId) ?? [])
                if (peer.Life > 0) fixedBodies.Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
            FixedBlockers = new BlockerSet(fixedBodies);
            current = new(fixedBodies);
            if (owner.PlayerSent && owner.PlayerLife > 0 && !owner.Phasing)
            {
                var player = owner.Walk.Position(now);
                current.Add(new(player.X, player.Y, LivingCollision.PlayerSize));
            }
            if (includePrevious) corrections = new(current);
            foreach (var (id, m) in owner.Monsters)
            {
                if (!LivingCollision.Blocks(m)) continue;
                var p = m.Walk.Position(now);
                int size = LivingCollision.Size(m);
                var blocker = new ClientPathfinder.Blocker(p.X, p.Y, size);
                int ci = current.Add(blocker), correction = corrections?.Add(blocker) ?? -1, previous = -1;
                if (corrections is not null && m.CollisionPosition is { } old && old != p)
                    previous = corrections.Add(new(old.X, old.Y, size));
                indices.Add(id, (Bodies.Count, ci, correction, previous));
                Bodies.Add((id, p.X, p.Y, size));
            }
            owner.Performance.Elapsed(LatencyPhase.BlockerBuild, startedAt);
        }

        public IReadOnlyList<ClientPathfinder.Blocker> For(uint self, bool includePrevious = false)
        {
            var set = includePrevious && corrections is not null ? corrections : current;
            if (!indices.TryGetValue(self, out var index)) return set;
            var views = includePrevious && corrections is not null ? correctionViews : currentViews;
            if (!views.TryGetValue(self, out var view))
                views[self] = view = set.Without(ReferenceEquals(set, current) ? index.Current : index.Correction,
                    ReferenceEquals(set, current) ? -1 : index.Previous);
            return view;
        }

        public void Move(uint id, int x, int y)
        {
            var index = indices[id];
            var before = Bodies[index.Body];
            var blocker = new ClientPathfinder.Blocker(x, y, before.Size);
            current.Replace(index.Current, blocker);
            corrections?.Replace(index.Correction, blocker);
            Bodies[index.Body] = (id, x, y, before.Size);
        }

        public void CompleteCorrection(uint id, int x, int y)
        {
            var index = indices[id];
            if (index.Previous >= 0) corrections!.Replace(index.Previous, new(x, y, Bodies[index.Body].Size));
        }

        public void Dispose() => owner.monsterFrame = outer;
    }
}
