using System.Diagnostics;
using GameServer.Core.World;

public sealed partial class ClientState
{
    MonsterBlockerFrame? monsterFrame;
    // The frame the last path search outside a pass was answered from, and a list to read the next one's bodies into
    // (MonsterBlockers). Under Monsters.
    MonsterBlockerFrame? keptFrame;
    BodyList? spareBodies;

    /// <summary>One lock-owned position/index snapshot shared by all path orders in an AI or correction pass.</summary>
    public MonsterBlockerFrame BeginMonsterBlockerFrame(double now)
    {
        if (!Monitor.IsEntered(Monsters)) throw new SynchronizationLockException("A blocker frame requires the combat lock.");
        var frame = new MonsterBlockerFrame(this, now, monsterFrame);
        monsterFrame = frame;
        return frame;
    }

    /// <summary>The bodies a blocker frame is made of, read at one instant in the order the frame takes them. A frame is
    /// made from this list alone: two equal lists give frames that answer every question alike.</summary>
    internal sealed class BodyList
    {
        public readonly List<ClientPathfinder.Blocker> Fixed = [];
        public readonly List<(uint Id, int X, int Y)> Copies = [];
        public readonly List<(uint Id, ClientPathfinder.Blocker Body)> Monsters = [];
        public (int X, int Y)? Player;
        public bool Phasing;

        public void Read(ClientState owner, double now, bool placement)
        {
            Fixed.Clear(); Copies.Clear(); Monsters.Clear();
            Player = null;
            Fixed.AddRange(owner.StationaryBlockers);
            // Another player stands where its published pose has it, unless this client runs a copy of it (PeerWalks):
            // then the copy's own cell is its body, and it moves with the copy's steps.
            foreach (var peer in owner.Instance?.PlayersExcept(owner.PeerId) ?? [])
                if (peer.Life > 0 && peer.Owner is not null && (placement || !owner.PeerWalks.ContainsKey(peer.Id)))
                    Fixed.Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
            if (!placement)
                foreach (var (id, copy) in owner.PeerWalks)
                {
                    if (owner.Instance?.Player(id) is { Life: 0 }) continue;
                    var p = copy.Position(now);
                    Copies.Add((id, p.X, p.Y));
                }
            if (owner.PlayerSent && owner.PlayerLife > 0 && !owner.IsPhasing) Player = owner.Walk.Position(now);
            // Runtime collision follows native bodies. Proposed spawning deliberately retains its existing
            // clearance policy; it is not evidence of GGG's authoritative spawn-placement algorithm.
            if (placement)
                foreach (var (id, m) in owner.Monsters)
                {
                    if (!LivingCollision.StepsWithBodies(m)) continue;
                    var p = m.Walk.Position(now);
                    Monsters.Add((id, new(p.X, p.Y, LivingCollision.Size(m))));
                }
            else if (owner.DormantMonsters.Count == 0)
                foreach (var (id, m) in owner.Monsters)
                {
                    if (owner.MonsterBody(id, m, now) is { } body) Monsters.Add((id, body));
                }
            else
                foreach (var (id, m) in owner.CollisionMonsters)
                    if (owner.MonsterBody(id, m, now) is { } body) Monsters.Add((id, body));
            // Phasing (Quartz Flask): the player and the monsters pass each other. Read again: it ends on the clock.
            Phasing = owner.IsPhasing;
        }

        public bool Same(BodyList other)
        {
            if (Phasing != other.Phasing || Player != other.Player || Fixed.Count != other.Fixed.Count
                || Copies.Count != other.Copies.Count || Monsters.Count != other.Monsters.Count) return false;
            for (int i = 0; i < Monsters.Count; i++)
                if (Monsters[i].Id != other.Monsters[i].Id || Monsters[i].Body != other.Monsters[i].Body) return false;
            for (int i = 0; i < Fixed.Count; i++)
                if (Fixed[i] != other.Fixed[i]) return false;
            for (int i = 0; i < Copies.Count; i++)
                if (Copies[i] != other.Copies[i]) return false;
            return true;
        }
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
        /// <summary>What the frame was made from. Not to be changed while the frame is in use.</summary>
        internal readonly BodyList Read;

        public MonsterBlockerFrame(ClientState owner, double now, MonsterBlockerFrame? outer, bool placement = false)
            : this(owner, outer, ReadBodies(owner, now, placement, out long startedAt), startedAt) { }

        static BodyList ReadBodies(ClientState owner, double now, bool placement, out long startedAt)
        {
            startedAt = Stopwatch.GetTimestamp();
            var read = new BodyList();
            read.Read(owner, now, placement);
            return read;
        }

        internal MonsterBlockerFrame(ClientState owner, MonsterBlockerFrame? outer, BodyList read, long startedAt)
        {
            this.owner = owner; this.outer = outer; Read = read;
            FixedBlockers = new BlockerSet(read.Fixed);
            current = new(read.Fixed);
            foreach (var (id, x, y) in read.Copies)
            {
                indices.Add(id, (Bodies.Count, current.Add(new(x, y, LivingCollision.PlayerSize))));
                Bodies.Add((id, x, y, LivingCollision.PlayerSize));
            }
            if (read.Player is { } player) playerBody = current.Add(new(player.X, player.Y, LivingCollision.PlayerSize));
            foreach (var (id, blocker) in read.Monsters)
            {
                indices.Add(id, (Bodies.Count, current.Add(blocker)));
                Bodies.Add((id, blocker.X, blocker.Y, blocker.Size));
            }
            forPlayer = read.Phasing ? FixedBlockers : playerBody >= 0 ? current.Without(playerBody) : current;
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
            // A monster that phases through objects is stopped by no living body (LivingCollision.Phases).
            if (owner.Monsters.TryGetValue(self, out var mover) && LivingCollision.Phases(mover)) return owner.StationaryBlockers;
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

        /// <summary>The body is gone from here on (a corpse cleared in its own turn of a frame).</summary>
        public void Remove(uint id)
        {
            if (!indices.Remove(id, out var index)) return;
            current.Remove(index.Current);
            Bodies[index.Body] = (id, current[index.Current].X, current[index.Current].Y, -1);
        }

        public void Dispose() => owner.monsterFrame = outer;
    }
}
