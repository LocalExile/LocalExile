using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    static readonly ConditionalWeakTable<ClientState, FortificationSession> FortificationSessions = new();
    static readonly ConcurrentDictionary<uint, (DateTime At, FortificationState.Transfer State)> CarriedFortification = new();
    internal static FortificationSession FortificationOf(ClientState conn) =>
        FortificationSessions.GetValue(conn, _ => new());

    static void EnsureFortificationPools(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var session = FortificationOf(conn);
            session.Read(conn.Clock.Elapsed.TotalSeconds);
            if (conn.Pools.FortificationRevision != session.Revision) conn.Pools = PoolsFor(conn, conn.Pools.Level);
            ConfigureFortificationRecovery(conn, conn.Pools.Life);
        }
    }

    internal static decimal RawFortificationDamage(DamagePacket rolled, bool critical, int multiplier)
    {
        decimal total = rolled.Components.Sum(c => (decimal)c.Range.Min);
        return Math.Max(0, total) * (critical ? Math.Max(0, multiplier) / 100m : 1m);
    }

    static Dictionary<uint, int> WithFortification(ClientState conn, Dictionary<uint, int> sources)
    {
        lock (conn.Monsters)
        {
            int Get(string name) => Stats.Key(name) is uint key
                ? conn.PlayerStats.TryGetValue(key, out int set) ? set : sources.GetValueOrDefault(key) : 0;
            var session = FortificationOf(conn);
            if (conn.PlayerLife == 0 && session.Value.Uncapped > 0) session.Clear();
            var value = session.Observe(conn.Clock.Elapsed.TotalSeconds, Get);
            void Add(string name, int added)
            {
                if (Stats.Key(name) is uint key)
                    sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + added, int.MinValue, int.MaxValue);
            }
            Add(FortificationSession.AttackSpeed, value.IncreasedAttackSpeedPercent(session.SpeedPerStack));
            AddFortificationCounts(sources, value);
            return sources;
        }
    }

    static void AddFortificationCounts(Dictionary<uint, int> stats, FortificationState.Value value)
    {
        foreach (var (name, amount) in new[] { (FortificationSession.Current, value.Current),
            (FortificationSession.Uncapped, (int)Math.Min(int.MaxValue, value.Uncapped)),
            (FortificationSession.Maximum, value.Maximum), ("has_fortify", value.Current > 0 ? 1 : 0),
            ("has_fortification", value.Current > 0 ? 1 : 0) })
            if (Stats.Key(name) is uint key) stats[key] = amount;
    }

    // Called only at an accepted melee damage commit, before any notification can yield to another action/area.
    internal static bool FortificationOnHitLocked(ClientState conn, SpawnedMonster target, decimal rawDamage,
        bool melee, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill = null, Random? random = null)
    {
        if (!melee || !target.Hostile || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
        int Get(string name)
        {
            if (Stats.Key(name) is not uint key) return 0;
            return (int)Math.Clamp((long)player.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0),
                int.MinValue, int.MaxValue);
        }
        return FortificationOf(conn).Hit(conn.Clock.Elapsed.TotalSeconds, rawDamage, target.MaxLife,
            target.Rarity, Get, random ?? Random.Shared);
    }

    internal static int FortificationDamageTaken(ClientState conn, int damage)
    {
        lock (conn.Monsters)
        {
            var value = FortificationOf(conn).Read(conn.Clock.Elapsed.TotalSeconds);
            return (int)((long)Math.Max(0, damage) * (100 - Math.Min(100, value.Current)) / 100);
        }
    }

    static void ConfigureFortificationRecovery(ClientState conn, uint maximumLife)
    {
        lock (conn.Monsters)
        {
            FortificationOf(conn).ConfigureRecovery(conn.Clock.Elapsed.TotalSeconds, maximumLife,
                Stats.Key(FortificationSession.Regeneration) is uint key && conn.PlayerStats.ContainsKey(key));
        }
    }

    static void CarryFortification(ClientState conn, uint ticket)
    {
        lock (conn.Monsters)
        {
            var session = FortificationOf(conn);
            if (conn.PlayerLife > 0)
            {
                var transfer = session.State.Carry(conn.Clock.Elapsed.TotalSeconds);
                if (transfer.Grants.Count > 0) CarriedFortification[ticket] = (DateTime.UtcNow, transfer);
            }
            session.Clear();
        }
        foreach (var (old, entry) in CarriedFortification)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedFortification.TryRemove(old, out _);
    }

    static async Task RunFortification(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        RestoreFortification(conn);
        while (!conn.Closed)
        {
            await Task.Delay(32);
            if (conn.Closed) break;
            try
            {
                await SendFortification(remote, stream, ciphers, conn);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
        }
    }

    internal static void RestoreFortification(ClientState conn)
    {
        lock (conn.Monsters)
            if (CarriedFortification.TryRemove(conn.Ticket, out var entry)
                && DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && conn.PlayerLife > 0)
                FortificationOf(conn).Restore(entry.State, conn.Clock.Elapsed.TotalSeconds);
    }

    // Construct buffs, stats and Actor records together inside the send gate from the current owner state.
    internal static Task SendFortification(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            using (conn.Performance.Lock(conn.Monsters, "fortification"))
            {
                if (conn.Closed || conn.LeavingArea || conn.Paused) return null;
                var session = FortificationOf(conn);
                double now = conn.Clock.Elapsed.TotalSeconds;
                using var snapshot = session.Capture(now);
                if (conn.PlayerLife == 0) { if (session.Value.Uncapped > 0) session.Clear(); }
                session.Read(conn.Clock.Elapsed.TotalSeconds);
                if (session.PresentedRevision == session.Revision) return null;
                ReconcileOwnAuras(conn);
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                return WithOwnAuraChanges(conn, () =>
                {
                    // Build and acknowledge the tail AFTER any aura prefix, in the same order as its wire packets.
                    var value = session.Read(conn.Clock.Elapsed.TotalSeconds);
                    var packets = new List<byte[]>();
                    if (session.Buff != 0) packets.Add(InstanceEntities.RemoveBuff(PlayerId, session.Buff));
                    session.Buff = 0;
                    if (value.Current > 0)
                    {
                        session.Buff = conn.NextBuff++;
                        packets.Add(InstanceEntities.AddBuff(PlayerId, session.Buff, 1943, PlayerId,
                            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0,
                                value.NextExpiry!.Value - now)),
                            stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
                    }
                    packets.Add(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                    packets.Add(InstanceEntities.PlayerSkillsChange(PlayerId, PlayerSkills(conn), []));
                    session.PresentedRevision = session.Revision;
                    return packets.SelectMany(p => p).ToArray();
                });
            }
        });
}
