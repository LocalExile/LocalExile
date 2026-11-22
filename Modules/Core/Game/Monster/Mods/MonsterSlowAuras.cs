using GameServer.Core.Protocol.Instance;
using AuraState = (bool Emits, uint Source, int Rate, int Extra);

public static partial class Game
{
    readonly record struct SlowSource(uint Id, SpawnedMonster Monster, double RadiusSquared, int Rate);
    static readonly RuleTable<IReadOnlyDictionary<uint, SpawnedMonster>, List<SlowSource>> EnemySlowSources = new();

    // The runner gathers emitters once. Each party member samples its own position when its update runs.
    static (uint Source, int Rate) EnemySlowAt(IReadOnlyDictionary<uint, SpawnedMonster> monsters,
        List<SlowSource> fields, (int X, int Y) at, double now)
    {
        (uint Source, int Rate) best = default;
        foreach (var field in fields)
        {
            if (field.Rate > best.Rate || field.Rate == best.Rate && field.Id >= best.Source) continue;
            var m = field.Monster;
            if (!m.Hostile || m.Dead || m.Life == 0 || m.WorldHidden ||
                !monsters.TryGetValue(field.Id, out var current) || !ReferenceEquals(m, current)) continue;
            var from = m.Walk.Position(now);
            double dx = (double)from.X - at.X, dy = (double)from.Y - at.Y;
            if (dx * dx + dy * dy <= field.RadiusSquared) best = (field.Id, field.Rate);
        }
        return best;
    }

    static AuraState EnemySlowOn(SpawnedMonster monster)
    {
        if (monster.Dead || monster.Life == 0 || monster.WorldHidden) return default;
        var move = monster.SpeedAura.Movement;
        return (monster.EmitsEnemySlow, move.Rate < 0 ? move.Source : 0, Math.Min(0, move.Rate), 0);
    }

    static void UpdatePlayerEnemySlow(ClientState conn, double now)
    {
        var slow = EnemySlowSources.TryGetValue(conn.Monsters, out var fields) && fields.Count != 0
            && !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0 && !InGracePeriod(conn)
            ? EnemySlowAt(conn.Monsters, fields, conn.Walk.Position(now), now) : default;
        SetPlayerEnemySlow(conn, slow, now);
    }

    public static void ClearPlayerEnemySlow(ClientState conn)
    {
        lock (conn.Monsters) SetPlayerEnemySlow(conn, default, AreaTime(conn));
    }

    internal sealed class EnemySlowCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var conn in Program.InGame.Keys)
                lock (conn.Monsters)
                    if (conn.EnemySlow.Source != 0) SetPlayerEnemySlow(conn, default, AreaTime(conn));
        }
    }

    static void SetPlayerEnemySlow(ClientState conn, (uint Source, int Rate) slow, double now)
    {
        int before = conn.EnemySlow.Rate;
        conn.EnemySlow = slow;
        if (before == slow.Rate) return;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        conn.ApplyMovementSpeedAt(now);
    }

    static int PlayerMovementPct(ClientState conn) => conn.Pools.MovementVelocityPct + conn.Rampage.Current.MovementPercent;

    static int CompanionMovementPct(SpawnedMonster monster, double now) => MonsterWalkState(monster, now).Pct;

    static double CompanionWalkSpeed(SpawnedMonster monster, double now) =>
        MonsterWalkSpeedAt(monster, monster.Effects.MovementPct) * MonsterWalkState(monster, now).Factor;

    static void PresentOwnedMinionSlows(ClientState conn, WorldRecordBuilder packets, double now)
    {
        if (conn.EnemySlowViews.Count == 0 && !(EnemySlowSources.TryGetValue(conn.Monsters, out var fields) && fields.Count != 0)) return;
        foreach (var (id, shown) in conn.EnemySlowViews)
            if (shown.OwnedMinion && !conn.MinionOrigins.ContainsKey(id)) conn.EnemySlowViews.Remove(id);
        foreach (var (id, _) in conn.MinionOrigins)
            if (conn.Monsters.TryGetValue(id, out var minion))
                PresentReceivedSlow(conn, packets, id, EnemySlowOn(minion).Source, CompanionMovementPct(minion, now), ownedMinion: true);
            else conn.EnemySlowViews.Remove(id);
    }

    // Players and owned summons have per-observer object ids. Present their received buff after both births.
    static void PresentReceivedSlow(ClientState conn, WorldRecordBuilder packets, uint id, uint source, int movement, double? peerSpeed = null, bool ownedMinion = false)
    {
        bool known = conn.EnemySlowViews.TryGetValue(id, out var shown);
        if (source == 0 && !known || conn.LockstepClient && !conn.WorldOutput.PublishedObject(id).IsReady) return;
        if (!known || shown.Movement != movement)
        {
            var stats = InstanceEntities.StatsUpdate(id, [(StatKeys.MovementVelocityPct, movement)]);
            if (id == PlayerId) RelayWalkForStats(conn, stats);
            else if (peerSpeed is double speed && conn.LockstepFrame is { } frame && conn.PeerWalks.TryGetValue(id, out var walk))
            {
                StepWalkers(conn, frame.Start);
                walk.SetSpeed(speed, frame.Start);
            }
            packets.Presentation(id, stats);
        }
        if (shown.Source != source)
        {
            if (shown.Buff != 0) packets.Presentation(id, InstanceEntities.RemoveBuff(id, shown.Buff));
            shown = default;
            if (source != 0 && conn.MonsterViews.ContainsKey(source) &&
                (!conn.LockstepClient || conn.WorldOutput.PublishedObject(source).IsReady))
            {
                shown.Source = source;
                shown.Buff = conn.NextBuff++;
                packets.Presentation(id, InstanceEntities.AddBuff(id, shown.Buff, EnemySlowBuff, source));
            }
        }
        if (source == 0) conn.EnemySlowViews.Remove(id);
        else conn.EnemySlowViews[id] = (shown.Source, shown.Buff, movement, ownedMinion);
    }
}
