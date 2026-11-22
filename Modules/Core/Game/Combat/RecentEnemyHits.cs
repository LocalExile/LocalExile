using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Read sources, count and clock together. Development input cannot replace either authoritative output.
    public static (int Count, int Chance) RecentEnemyHitStatsLocked(Player player, IReadOnlyDictionary<uint, int> sources)
    {
        int count = player.RecentEnemyHits.Count(AreaTime(player));
        int Get(uint key) => sources.GetValueOrDefault(key);
        return (count, RecentEnemyHits.Chance(count, Get(RecentEnemyHits.PerHit), Get(RecentEnemyHits.GenericChance)));
    }

    public static void AddRecentEnemyHitStatsLocked(Player player, StatMap stats)
    {
        int perHit = stats.GetValueOrDefault(RecentEnemyHits.PerHit);
        int generic = stats.GetValueOrDefault(RecentEnemyHits.GenericChance);
        if (perHit == 0 && generic == 0)
        {
            if (stats.GetValueOrDefault(RecentEnemyHits.Counter) != 0) stats[RecentEnemyHits.Counter] = 0;
            if (stats.GetValueOrDefault(RecentEnemyHits.VirtualChance) != 0) stats[RecentEnemyHits.VirtualChance] = 0;
            return;
        }
        var value = RecentEnemyHitStatsLocked(player, stats);
        if (player.RecentEnemyHits.NextChangeIn(AreaTime(player)) is double left) StatsChangeIn(left);
        if (RecentEnemyHits.Counter is var counter) stats[counter] = value.Count;
        if (RecentEnemyHits.VirtualChance is var chance) stats[chance] = value.Chance;
    }

    public static RecentEnemyHits.Decision IncomingArmourLocked(Player player, bool relevant, int priorContacts,
        IReadOnlyDictionary<uint, int> stats)
    {
        // The caller captures membership before ALL stat readers, which may prune a later clock boundary.
        int Get(uint key) => stats.GetValueOrDefault(key);
        int chance = RecentEnemyHits.Chance(priorContacts, Get(RecentEnemyHits.PerHit), Get(RecentEnemyHits.GenericChance));
        return RecentEnemyHits.Resolve(player.Pools.Armour, chance, relevant, player.IncomingHitRandom);
    }

    // A stat presentation (the RecentHits step times the expiry). Sample inside the shared send gate, including when
    // a queued refresh crosses a contact, death, modifier change or exact expiry boundary.
    public static Task SendRecentEnemyHits(Player player) =>
        Send(player, [], null, atGate: () =>
        {
            if (player.Closed || player.LeavingArea || player.Paused) return null;
            var sources = PlayerStatValues(player);
            if (sources.GetValueOrDefault(RecentEnemyHits.PerHit) == 0
                && sources.GetValueOrDefault(RecentEnemyHits.GenericChance) == 0
                && player.PresentedRecentEnemyHits == (0, 0))
                return null;
            var value = RecentEnemyHitStatsLocked(player, sources);
            if (player.PresentedRecentEnemyHits == value) return null;
            var stats = new Dictionary<uint, int>();
            if (RecentEnemyHits.Counter is var counter) stats[counter] = value.Count;
            if (RecentEnemyHits.VirtualChance is var chance) stats[chance] = value.Chance;
            return PlayerStatsUpdateAtGate(player, stats.Select(kv => (kv.Key, kv.Value)).ToList());
        });
}
