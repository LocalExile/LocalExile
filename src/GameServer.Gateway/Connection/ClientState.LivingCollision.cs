using GameServer.Core.World;

sealed partial class ClientState
{
    public IReadOnlyList<ClientPathfinder.Blocker> CorrectionBlockers()
    {
        var result = new List<ClientPathfinder.Blocker>(StationaryBlockers);
        if (Phasing) return result;
        // Deterministic arrival arbitration: the later actor moves, instead of both choosing the same exit.
        foreach (var peer in Instance?.PlayersExcept(PeerId) ?? [])
            if (peer.Id < PeerId && peer.Life > 0) result.Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
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

    public PlayerWalk NewMonsterWalk(uint id, int x, int y, double speed, int size, bool unique) =>
        new(x, y, Math.Max(1, speed), (cx, cy) => Layout.WalkableFor(cx, cy, Math.Max(0, size)) && !Barrier.Contains((cx, cy)),
            Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096,
            () => size < 0 ? StationaryBlockers : MonsterBlockers(id), Math.Max(0, size),
            // The client's budget for this mover (0x141e4d890): 1000 expansions for a unique, 100 for any other monster.
            limit: unique ? ClientPathfinder.SearchLimit.Expansions1000 : ClientPathfinder.SearchLimit.Expansions100);
}
