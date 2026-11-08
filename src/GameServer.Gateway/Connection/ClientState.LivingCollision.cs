using GameServer.Core.World;

sealed partial class ClientState
{
    readonly Dictionary<uint, (SpawnedMonster Monster, ClientPathfinder.Blocker Body)> corpseBodies = [];

    // A dead actor remains in the native pathfinder until its Positioned clear reaches this client.
    // Keep its death cell, not its abandoned walk's later analytical position. Under Monsters.
    public void RetainCorpseBody(uint id, SpawnedMonster monster, double at)
    {
        if (!LockstepClient || LivingCollision.Size(monster) < 0 || monster.Variety?.PositionedBlocking == false) return;
        var p = monster.Walk.Position(at);
        corpseBodies[id] = (monster, new(p.X, p.Y, LivingCollision.Size(monster)));
    }

    public void ReleaseCorpseBody(uint id, SpawnedMonster monster)
    {
        if (corpseBodies.TryGetValue(id, out var corpse) && ReferenceEquals(corpse.Monster, monster)) corpseBodies.Remove(id);
    }

    public ClientPathfinder.Blocker? MonsterBody(uint id, SpawnedMonster monster, double at)
    {
        if (LivingCollision.Blocks(monster))
        {
            var p = monster.Walk.Position(at);
            return new(p.X, p.Y, LivingCollision.Size(monster));
        }
        return corpseBodies.TryGetValue(id, out var corpse) && ReferenceEquals(corpse.Monster, monster) ? corpse.Body : null;
    }

    public IReadOnlyList<ClientPathfinder.Blocker> CorrectionBlockers()
    {
        var result = new List<ClientPathfinder.Blocker>(StationaryBlockers);
        if (IsPhasing) return result;
        // Deterministic arrival arbitration: the later actor moves, instead of both choosing the same exit.
        foreach (var peer in Instance?.PlayersExcept(PeerId) ?? [])
            if (peer.Id < PeerId && peer.Life > 0 && peer.Owner is not null) result.Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
        return new BlockerSet(result);
    }

    public IReadOnlyList<ClientPathfinder.Blocker> MonsterBlockers(uint self)
    {
        lock (Monsters)
        {
            if (monsterFrame is { } frame) return frame.For(self);
            using var snapshot = BeginMonsterBlockerFrame(Clock.Elapsed.TotalSeconds);
            return snapshot.For(self);
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

    public PlayerWalk NewMonsterWalk(uint id, int x, int y, double speed, int size, bool unique) =>
        new(x, y, Math.Max(1, speed), (cx, cy) => Layout.WalkableFor(cx, cy, Math.Max(0, size)) && !Barrier.Contains((cx, cy)),
            Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096,
            () => size < 0 ? StationaryBlockers : MonsterBlockers(id), Math.Max(0, size),
            // The client's budget for this mover (0x141e50f90): 1000 expansions for a unique, 100 for any other monster.
            limit: unique ? ClientPathfinder.SearchLimit.Expansions1000 : ClientPathfinder.SearchLimit.Expansions100, sight: Layout.Sight)
        { FrameOf = LockstepFrameOf };
}
