using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Read sources, count and clock together. Development input cannot replace either authoritative output.
    public static (int Count, int Chance) RecentEnemyHitStatsLocked(Player conn, IReadOnlyDictionary<uint, int> sources)
    {
        int count = conn.RecentEnemyHits.Count(AreaTime(conn));
        int Get(uint key) => sources.GetValueOrDefault(key);
        return (count, RecentEnemyHits.Chance(count, Get(RecentEnemyHits.PerHit), Get(RecentEnemyHits.GenericChance)));
    }

    public static void AddRecentEnemyHitStatsLocked(Player conn, StatMap stats)
    {
        var value = RecentEnemyHitStatsLocked(conn, stats);
        if (conn.RecentEnemyHits.NextChangeIn(AreaTime(conn)) is double left) StatsChangeIn(left);
        if (RecentEnemyHits.Counter is var counter) stats[counter] = value.Count;
        if (RecentEnemyHits.VirtualChance is var chance) stats[chance] = value.Chance;
    }

    public static RecentEnemyHits.Decision IncomingArmourLocked(Player conn, bool relevant, int priorContacts,
        IReadOnlyDictionary<uint, int> stats)
    {
        // The caller captures membership before ALL stat readers, which may prune a later clock boundary.
        int Get(uint key) => stats.GetValueOrDefault(key);
        int chance = RecentEnemyHits.Chance(priorContacts, Get(RecentEnemyHits.PerHit), Get(RecentEnemyHits.GenericChance));
        return RecentEnemyHits.Resolve(conn.Pools.Armour, chance, relevant, conn.IncomingHitRandom);
    }

    // A stat presentation (the RecentHits step times the expiry). Sample inside the shared send gate, including when
    // a queued refresh crosses a contact, death, modifier change or exact expiry boundary.
    public static Task SendRecentEnemyHits(Player conn) =>
        Send(conn, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.Paused) return null;
                var value = RecentEnemyHitStatsLocked(conn, CharacterPlayerStats(conn));
                if (conn.PresentedRecentEnemyHits == value) return null;
                var stats = new Dictionary<uint, int>();
                if (RecentEnemyHits.Counter is var counter) stats[counter] = value.Count;
                if (RecentEnemyHits.VirtualChance is var chance) stats[chance] = value.Chance;
                return PlayerStatsUpdateAtGate(conn, stats.Select(kv => (kv.Key, kv.Value)).ToList());
            }
        });
}
