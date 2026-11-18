using GameServer.Core.World;

public sealed partial class ClientState
{
    /// <summary>A killed monster on a lockstep client, from the kill until its body is out of the client's paths. The
    /// client's monster lives until the death reaches it: it walks on, and in the frame the death is written in
    /// (<see cref="Died"/>) its action is cancelled, so the walker takes that frame's steps and stands (the stop a hit's
    /// recovery makes). Its body then stays in the paths for <see cref="Ticks"/> ticks of the death clip, that frame's
    /// included, and leaves in its own turn of the last one; 0 = with the death itself.</summary>
    sealed class Corpse(SpawnedMonster monster, int ticks)
    {
        public readonly SpawnedMonster Monster = monster;
        public readonly int Ticks = ticks;
        public readonly TaskCompletionSource<double> Death = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The start of the frame the death was written in, and of the frame whose tick clears the body.</summary>
        public double? Died, Clears;
        /// <summary>Whether the body is in the paths at the start of the frame from <paramref name="start"/>.</summary>
        public bool Blocks(double start) => Died is null || Ticks > 0 && start < Clears + 1e-6;
    }

    readonly Dictionary<uint, Corpse> corpses = [];
    int corpsesAwaitingDeath;
    // The monsters whose death message this lockstep client has been written, by id (DeathWritten).
    readonly Dictionary<uint, SpawnedMonster> deathsWritten = [];

    /// <summary>Whether a kill is committed whose death message is not written yet (read without the lock).</summary>
    public bool CorpseAwaitsDeath => Volatile.Read(ref corpsesAwaitingDeath) > 0;

    // A dead actor remains in the native pathfinder until its death clip clears its body there. It keeps its walker
    // meanwhile (CorpseWalks): the body is where the client's monster stands. Whether a corpse is kept for the monster
    // after the call: one kept already stays as it is, and none is made for a monster without a body or for one whose
    // death was written before (its body has come and gone). Under Monsters.
    public bool RetainCorpseBody(uint id, SpawnedMonster monster, int ticks)
    {
        if (!LockstepClient || LivingCollision.Size(monster) < 0 || monster.Variety?.PositionedBlocking == false) return false;
        if (CorpseWalks(id, monster)) return true;
        if (DeathWritten(id, monster)) return false;
        ReleaseCorpseBody(id);
        corpses[id] = new(monster, ticks);
        corpsesAwaitingDeath++;
        return true;
    }

    /// <summary>Whether a death message of this monster has been written to this client. The client's monster dies with
    /// the first one, whichever producer writes it (the hit, or the periodic view of the monsters, which can come a
    /// frame before or after it); a later one changes nothing there. Under Monsters.</summary>
    public bool DeathWritten(uint id, SpawnedMonster monster) =>
        deathsWritten.TryGetValue(id, out var written) && ReferenceEquals(written, monster);

    /// <summary>A life message of the monster is being written: its death (0), or life again after one. Under Monsters.</summary>
    public void NoteLifeWritten(uint id, SpawnedMonster monster, uint life)
    {
        if (life == 0) deathsWritten[id] = monster;
        else deathsWritten.Remove(id);
    }

    public void ReleaseCorpseBody(uint id, SpawnedMonster monster)
    {
        if (corpses.TryGetValue(id, out var corpse) && ReferenceEquals(corpse.Monster, monster)) ReleaseCorpseBody(id);
    }

    void ReleaseCorpseBody(uint id)
    {
        if (!corpses.Remove(id, out var corpse)) return;
        if (corpse.Died is null) corpsesAwaitingDeath--;
        corpse.Death.TrySetCanceled();
    }

    /// <summary>Whether this id's kill still waits for its death message. Under Monsters.</summary>
    public bool CorpseAwaitsDeathOf(uint id) => corpses.TryGetValue(id, out var corpse) && corpse.Died is null;

    /// <summary>The corpse's death message is written in <paramref name="frame"/>: its walk ends with that frame's steps
    /// and its death clip starts. The walkers must be stepped up to the frame first. Under Monsters.</summary>
    public void CorpseDied(uint id, (double Start, double End) frame)
    {
        if (!corpses.TryGetValue(id, out var corpse) || corpse.Died is not null) return;
        corpse.Died = frame.Start;
        corpse.Clears = frame.Start + (corpse.Ticks - 1) * (frame.End - frame.Start);
        corpsesAwaitingDeath--;
        corpse.Monster.Walk.Cancel(frame.Start);
        corpse.Death.TrySetResult(frame.Start);
    }

    /// <summary>Completes with the start of the frame the corpse's death message was written in; cancelled when the
    /// corpse is released first. A corpse that is not kept has none.</summary>
    public Task<double>? CorpseDeath(uint id, SpawnedMonster monster) =>
        corpses.TryGetValue(id, out var corpse) && ReferenceEquals(corpse.Monster, monster) ? corpse.Death.Task : null;

    /// <summary>A kept corpse steps with the living walkers (the steps its client still takes). Under Monsters.</summary>
    public bool CorpseWalks(uint id, SpawnedMonster monster) =>
        corpses.TryGetValue(id, out var corpse) && ReferenceEquals(corpse.Monster, monster);

    /// <summary>Whether the corpse's body leaves the paths in its turn of the frame from <paramref name="start"/>.</summary>
    public bool CorpseClears(uint id, double start) =>
        corpses.TryGetValue(id, out var corpse) && corpse.Died is not null && (corpse.Ticks == 0 || start > corpse.Clears - 1e-6);

    public ClientPathfinder.Blocker? MonsterBody(uint id, SpawnedMonster monster, double at)
    {
        if (!LivingCollision.Blocks(monster))
        {
            if (!corpses.TryGetValue(id, out var corpse) || !ReferenceEquals(corpse.Monster, monster)) return null;
            // A caller on the wall clock asks about the frame being sent, as PlayerWalk.Position does.
            if (!corpse.Blocks(LockstepFrame is { } frame ? Math.Min(at, frame.Start) : at)) return null;
        }
        var p = monster.Walk.Position(at);
        return new(p.X, p.Y, LivingCollision.Size(monster));
    }

    /// <summary>The other players' walkers as this lockstep client runs them (by their id here). Each walks the orders its
    /// owner's client was sent, from the cell the copy stands on, and is stepped in this client's frames with every other
    /// walker, as a body among them (SharedMonsters.cs PeerRecordAtGate). Under Monsters.</summary>
    public readonly Dictionary<uint, PlayerWalk> PeerWalks = [];

    /// <summary>A walk for another player's copy on this client: a player's search and size, this client's floor, bodies
    /// and frames.</summary>
    public PlayerWalk NewPeerWalk(uint id, int x, int y, double speed)
    {
        var l = Layout;
        Func<int, int, bool>? walkable = l.Walkable is { } w ? (cx, cy) => w(cx, cy) && !Barrier.Contains((cx, cy)) : null;
        return new(x, y, Math.Max(1, speed), walkable, l.GridW > 0 ? l.GridW : 4096, l.GridH > 0 ? l.GridH : 4096,
            () => MonsterBlockers(id), LivingCollision.PlayerSize, sight: l.Sight) { FrameOf = LockstepFrameOf };
    }

    public IReadOnlyList<ClientPathfinder.Blocker> CorrectionBlockers()
    {
        var result = new List<ClientPathfinder.Blocker>(StationaryBlockers);
        // A lockstep client moves nobody out of another player's body, so neither does its estimate.
        if (IsPhasing || LockstepClient) return result;
        // Deterministic arrival arbitration: the later actor moves, instead of both choosing the same exit.
        foreach (var peer in Instance?.PlayersExcept(PeerId) ?? [])
            if (peer.Id < PeerId && peer.Life > 0 && peer.Owner is not null) result.Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
        return new BlockerSet(result);
    }

    /// <summary>What stands in a monster's way now. Inside a pass its frame answers. Outside one (an order placed at
    /// its send gate, a skill's walk) the bodies are read each time, and the frame made from them is kept for as long as
    /// they read the same: a crowd's orders of one client frame would otherwise each index every body again.</summary>
    public IReadOnlyList<ClientPathfinder.Blocker> MonsterBlockers(uint self)
    {
        lock (Monsters)
        {
            if (monsterFrame is { } frame) return frame.For(self);
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = Clock.Elapsed.TotalSeconds;
            var read = spareBodies ?? new BodyList();
            spareBodies = null;
            read.Read(this, now, placement: false);
            if (keptFrame is { } kept && kept.Read.Same(read))
            {
                spareBodies = read;
                Performance.Elapsed(LatencyPhase.BlockerBuild, startedAt);
                return kept.For(self);
            }
            // The frame given up is used by nobody (its views are read only inside the search they were asked for).
            spareBodies = keptFrame?.Read;
            keptFrame = new MonsterBlockerFrame(this, null, read, startedAt);
            return keptFrame.For(self);
        }
    }

    /// <summary>The existing new-spawn clearance policy, separate from native runtime body discovery.
    /// Established saved positions never use this proposal search.</summary>
    public IReadOnlyList<ClientPathfinder.Blocker> MonsterPlacementBlockers(uint self)
    {
        lock (Monsters)
        {
            // Do not reuse a runtime frame: it contains dormant bodies which this policy never considered.
            using var snapshot = new MonsterBlockerFrame(this, Clock.Elapsed.TotalSeconds, monsterFrame, placement: true);
            return snapshot.For(self);
        }
    }

    /// <param name="throughBodies">Its walk goes through living bodies (<see cref="MonsterMovement.ThroughBodies"/>).</param>
    public PlayerWalk NewMonsterWalk(uint id, int x, int y, double speed, int size, bool unique, bool throughBodies = false) =>
        new(x, y, Math.Max(1, speed), (cx, cy) => Layout.WalkableFor(cx, cy, Math.Max(0, size)) && !Barrier.Contains((cx, cy)),
            Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096,
            () => size < 0 || throughBodies ? StationaryBlockers : MonsterBlockers(id), Math.Max(0, size),
            // The client's budget for this mover (0x141e50f90): 1000 expansions for a unique, 100 for any other monster.
            limit: unique ? ClientPathfinder.SearchLimit.Expansions1000 : ClientPathfinder.SearchLimit.Expansions100, sight: Layout.Sight)
        { FrameOf = LockstepFrameOf };
}
