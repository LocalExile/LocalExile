using System.Diagnostics;
using GameServer.Core.World;

public sealed partial class Player
{
    MonsterBlockerFrame? monsterFrame;
    // Pooled top-level blocker frame reused across BeginMonsterBlockerFrame calls so 1,000-monster frames do not allocate
    // fresh BlockerSet/Dictionary/List buffers every tick.
    MonsterBlockerFrame? pooledFrame;
    // The frame the last path search outside a pass was answered from, and a list to read the next one's bodies into
    // (MonsterBlockers). In the area's frame.
    MonsterBlockerFrame? keptFrame;
    BodyList? spareBodies;

    /// <summary>One lock-owned position/index snapshot shared by all path orders in an AI or correction pass.</summary>
    public MonsterBlockerFrame BeginMonsterBlockerFrame(double now)
    {
        Area.AssertOwner();
        MonsterBlockerFrame frame;
        if (monsterFrame is null && pooledFrame is { } pooled)
        {
            pooledFrame = null;
            pooled.Reset(now, null);
            frame = pooled;
        }
        else frame = new MonsterBlockerFrame(this, now, monsterFrame);
        monsterFrame = frame;
        return frame;
    }

    /// <summary>The bodies a blocker frame is made of, read at one instant in the order the frame takes them. A frame is
    /// made from this list alone: two equal lists give frames that answer every question alike.</summary>
    internal sealed class BodyList
    {
        public readonly List<ClientPathfinder.Blocker> Fixed = [];
        public readonly List<ClientPathfinder.Blocker> PlayerFixed = [];
        public readonly List<(uint Id, int X, int Y)> Copies = [];
        public readonly List<(uint Id, ClientPathfinder.Blocker Body, bool BlocksPlayer)> Monsters = [];
        public (int X, int Y)? Player;
        public bool Phasing;
        public bool HasSeparatePlayerFixed;
        public bool HasAlliedMinion;

        public void Read(Player owner, double now, bool placement)
        {
            Fixed.Clear(); PlayerFixed.Clear(); Copies.Clear(); Monsters.Clear();
            Player = null;
            HasAlliedMinion = false;
            var stationary = owner.StationaryBlockers;
            var playerStationary = owner.PlayerStationaryBlockers;
            HasSeparatePlayerFixed = !ReferenceEquals(stationary, playerStationary);
            Fixed.AddRange(stationary);
            if (HasSeparatePlayerFixed) PlayerFixed.AddRange(playerStationary);
            // Another player stands where its own walk has it: every client runs the same player from the same orders.
            foreach (var peer in owner.Instance?.PlayersExcept(owner.Id) ?? [])
                if (peer.PlayerLife > 0 && peer is { } other)
                {
                    var p = other.Walk.Position(now);
                    ClientPathfinder.Blocker peerBody = new(p.X, p.Y, LivingCollision.PlayerSize);
                    Fixed.Add(peerBody);
                    if (HasSeparatePlayerFixed) PlayerFixed.Add(peerBody);
                }
            bool phasing = owner.IsPhasing;
            if (owner.Connection.PlayerSent && owner.PlayerLife > 0 && !phasing) Player = owner.Walk.Position(now);
            // Runtime collision follows native bodies. Proposed spawning deliberately retains its existing
            // clearance policy; it is not evidence of GGG's authoritative spawn-placement algorithm.
            var living = owner.Area.SyncLivingMonsters();
            if (placement)
            {
                for (int i = 0; i < living.Count; i++)
                {
                    var (id, m) = living[i];
                    if (!LivingCollision.StepsWithBodies(m)) continue;
                    var p = m.Walk.Position(now);
                    bool blocksPlayer = !m.AlliedMinion;
                    if (!blocksPlayer) HasAlliedMinion = true;
                    Monsters.Add((id, new(p.X, p.Y, LivingCollision.Size(m)), blocksPlayer));
                }
            }
            else
            {
                for (int i = 0; i < living.Count; i++)
                {
                    var (id, m) = living[i];
                    if (owner.MonsterBody(id, m, now) is { } body)
                    {
                        bool blocksPlayer = !m.AlliedMinion;
                        if (!blocksPlayer) HasAlliedMinion = true;
                        Monsters.Add((id, body, blocksPlayer));
                    }
                }
                if (owner.HasCorpses)
                {
                    foreach (uint id in owner.KeptCorpseIds)
                        if (owner.Area.Monsters.TryGetValue(id, out var m) && (m.Dead || m.Life == 0)
                            && owner.MonsterBody(id, m, now) is { } body)
                        {
                            bool blocksPlayer = !m.AlliedMinion;
                            if (!blocksPlayer) HasAlliedMinion = true;
                            Monsters.Add((id, body, blocksPlayer));
                        }
                }
                if (owner.Area.DormantMonsters.Count > 0)
                    foreach (var (id, m) in owner.Area.DormantMonsters)
                        if (owner.MonsterBody(id, m, now) is { } body)
                        {
                            bool blocksPlayer = !m.AlliedMinion;
                            if (!blocksPlayer) HasAlliedMinion = true;
                            Monsters.Add((id, body, blocksPlayer));
                        }
            }
            // Phasing (Quartz Flask): the player and the monsters pass each other.
            Phasing = phasing;
        }

        public bool Same(BodyList other)
        {
            if (Phasing != other.Phasing || HasSeparatePlayerFixed != other.HasSeparatePlayerFixed
                || HasAlliedMinion != other.HasAlliedMinion || Player != other.Player
                || Fixed.Count != other.Fixed.Count || PlayerFixed.Count != other.PlayerFixed.Count
                || Copies.Count != other.Copies.Count || Monsters.Count != other.Monsters.Count) return false;
            for (int i = 0; i < Monsters.Count; i++)
                if (Monsters[i].Id != other.Monsters[i].Id || Monsters[i].Body != other.Monsters[i].Body
                    || Monsters[i].BlocksPlayer != other.Monsters[i].BlocksPlayer) return false;
            for (int i = 0; i < Fixed.Count; i++)
                if (Fixed[i] != other.Fixed[i]) return false;
            for (int i = 0; i < PlayerFixed.Count; i++)
                if (PlayerFixed[i] != other.PlayerFixed[i]) return false;
            for (int i = 0; i < Copies.Count; i++)
                if (Copies[i] != other.Copies[i]) return false;
            return true;
        }
    }

    public sealed class MonsterBlockerFrame : IDisposable
    {
        readonly Player owner;
        MonsterBlockerFrame? outer;
        readonly BlockerSet fixedBlockers = new();
        readonly BlockerSet playerFixedBlockers = new();
        readonly BlockerSet current = new();
        readonly BlockerSet playerCurrent = new();
        readonly Dictionary<uint, (int Body, int Current, int PlayerCurrent)> indices = [];
        readonly Dictionary<uint, IReadOnlyList<ClientPathfinder.Blocker>> views = [];
        int playerBody = -1;
        IReadOnlyList<ClientPathfinder.Blocker> forPlayer = [];
        public readonly List<(uint Id, int X, int Y, int Size)> Bodies = [];
        public IReadOnlyList<ClientPathfinder.Blocker> FixedBlockers => fixedBlockers;
        /// <summary>What the frame was made from. Not to be changed while the frame is in use.</summary>
        internal readonly BodyList Read;

        public MonsterBlockerFrame(Player owner, double now, MonsterBlockerFrame? outer, bool placement = false)
            : this(owner, outer, ReadBodies(owner, now, placement, out long startedAt), startedAt, !placement) { }

        static BodyList ReadBodies(Player owner, double now, bool placement, out long startedAt)
        {
            startedAt = Stopwatch.GetTimestamp();
            var read = new BodyList();
            read.Read(owner, now, placement);
            return read;
        }

        // Only a pass frame (BeginMonsterBlockerFrame) is asked for ForPlayer; the others skip the player sets.
        internal MonsterBlockerFrame(Player owner, MonsterBlockerFrame? outer, BodyList read, long startedAt,
            bool playerView = false)
        {
            this.owner = owner;
            Read = read;
            Populate(outer, startedAt, playerView);
        }

        internal void Reset(double now, MonsterBlockerFrame? nextOuter, bool placement = false)
        {
            long startedAt = Stopwatch.GetTimestamp();
            Read.Read(owner, now, placement);
            Populate(nextOuter, startedAt, playerView: !placement);
        }

        void Populate(MonsterBlockerFrame? nextOuter, long startedAt, bool playerView)
        {
            outer = nextOuter;
            indices.Clear();
            views.Clear();
            Bodies.Clear();
            playerBody = -1;
            fixedBlockers.Reset(Read.Fixed);
            current.Reset(Read.Fixed);
            bool separatePlayer = playerView && (Read.HasSeparatePlayerFixed || Read.HasAlliedMinion);
            bool buildPlayerCurrent = separatePlayer && !Read.Phasing;
            if (separatePlayer)
            {
                var pFixed = Read.HasSeparatePlayerFixed ? Read.PlayerFixed : Read.Fixed;
                playerFixedBlockers.Reset(pFixed);
                if (buildPlayerCurrent) playerCurrent.Reset(pFixed);
            }
            for (int i = 0; i < Read.Copies.Count; i++)
            {
                var (id, x, y) = Read.Copies[i];
                ClientPathfinder.Blocker copyBody = new(x, y, LivingCollision.PlayerSize);
                int pIndex = buildPlayerCurrent ? playerCurrent.Add(copyBody) : -1;
                indices.Add(id, (Bodies.Count, current.Add(copyBody), pIndex));
                Bodies.Add((id, x, y, LivingCollision.PlayerSize));
            }
            if (Read.Player is { } player) playerBody = current.Add(new(player.X, player.Y, LivingCollision.PlayerSize));
            for (int i = 0; i < Read.Monsters.Count; i++)
            {
                var (id, blocker, blocksPlayer) = Read.Monsters[i];
                int pIndex = buildPlayerCurrent && blocksPlayer ? playerCurrent.Add(blocker) : -1;
                indices.Add(id, (Bodies.Count, current.Add(blocker), pIndex));
                Bodies.Add((id, blocker.X, blocker.Y, blocker.Size));
            }
            forPlayer = !separatePlayer
                ? (Read.Phasing ? fixedBlockers : playerBody >= 0 ? current.Without(playerBody) : current)
                : (Read.Phasing ? playerFixedBlockers : playerCurrent);
            owner.Connection.Performance.Elapsed(LatencyPhase.BlockerBuild, startedAt);
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
            if (owner.Area.Monsters.TryGetValue(self, out var mover) && LivingCollision.Phases(mover)) return owner.StationaryBlockers;
            if (!indices.TryGetValue(self, out var index)) return current;
            if (!views.TryGetValue(self, out var view)) views[self] = view = current.Without(index.Current, -1);
            return view;
        }

        public void Move(uint id, int x, int y)
        {
            // A nonblocking actor still takes movement turns, but has no body entry to update.
            if (!indices.TryGetValue(id, out var index)) return;
            var before = Bodies[index.Body];
            ClientPathfinder.Blocker moved = new(x, y, before.Size);
            current.Replace(index.Current, moved);
            if (index.PlayerCurrent >= 0) playerCurrent.Replace(index.PlayerCurrent, moved);
            Bodies[index.Body] = (id, x, y, before.Size);
        }

        /// <summary>The body is gone from here on (a corpse cleared in its own turn of a frame).</summary>
        public void Remove(uint id)
        {
            if (!indices.Remove(id, out var index)) return;
            current.Remove(index.Current);
            if (index.PlayerCurrent >= 0) playerCurrent.Remove(index.PlayerCurrent);
            Bodies[index.Body] = (id, current[index.Current].X, current[index.Current].Y, -1);
        }

        public void Dispose()
        {
            owner.monsterFrame = outer;
            if (outer is null && owner.pooledFrame is null && !ReferenceEquals(owner.keptFrame, this))
                owner.pooledFrame = this;
        }
    }
}
