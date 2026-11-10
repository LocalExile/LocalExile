using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static readonly ConditionalWeakTable<ClientState, FortificationSession> FortificationSessions = new();
    public static readonly ConcurrentDictionary<uint, (DateTime At, FortificationState.Transfer State)> CarriedFortification = new();
    public static FortificationSession FortificationOf(ClientState conn) =>
        FortificationSessions.GetValue(conn, _ => new());

    public static void EnsureFortificationPools(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var session = FortificationOf(conn);
            session.Read(conn.Clock.Elapsed.TotalSeconds);
            if (conn.Pools.FortificationRevision != session.Revision) conn.Pools = PoolsFor(conn, conn.Pools.Level);
        }
    }

    public static decimal RawFortificationDamage(DamagePacket rolled, bool critical, int multiplier)
    {
        decimal total = rolled.Components.Sum(c => (decimal)c.Range.Min);
        return Math.Max(0, total) * (critical ? Math.Max(0, multiplier) / 100m : 1m);
    }

    public static Dictionary<uint, int> WithFortification(ClientState conn, Dictionary<uint, int> sources)
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

    public static void AddFortificationCounts(Dictionary<uint, int> stats, FortificationState.Value value)
    {
        foreach (var (name, amount) in new[] { (FortificationSession.Current, value.Current),
            (FortificationSession.Uncapped, (int)Math.Min(int.MaxValue, value.Uncapped)),
            (FortificationSession.Maximum, value.Maximum), ("has_fortify", value.Current > 0 ? 1 : 0),
            ("has_fortification", value.Current > 0 ? 1 : 0) })
            if (Stats.Key(name) is uint key) stats[key] = amount;
    }

    // Called only at an accepted melee damage commit, before any notification can yield to another action/area.
    public static bool FortificationOnHitLocked(ClientState conn, SpawnedMonster target, decimal rawDamage,
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

    public static int FortificationDamageTaken(ClientState conn, int damage)
    {
        lock (conn.Monsters)
        {
            var value = FortificationOf(conn).Read(conn.Clock.Elapsed.TotalSeconds);
            return (int)((long)Math.Max(0, damage) * (100 - Math.Min(100, value.Current)) / 100);
        }
    }

    public static void ConfigureFortificationRecovery(ClientState conn, uint maximumLife,
        IReadOnlyDictionary<uint, int> sources)
    {
        var recovery = new Dictionary<uint, int>(sources);
        lock (conn.Monsters)
            foreach (var (key, value) in conn.PlayerStats) recovery[key] = value;
        UtilityFlasks.AddRawStats(recovery,
            conn.UtilityFlaskEffects.Stats().Concat(conn.FlaskGrounds.PlayerStats()), Stats);
        ConfigureFortificationRecovery(conn, maximumLife,
            name => Stats.Key(name) is uint key ? recovery.GetValueOrDefault(key) : 0);
    }

    public static void ConfigureFortificationRecovery(ClientState conn, uint maximumLife, Func<string, int> get)
    {
        lock (conn.Monsters)
        {
            FortificationOf(conn).ConfigureRecovery(conn.Clock.Elapsed.TotalSeconds, maximumLife,
                Stats.Key(FortificationSession.Regeneration) is uint key && conn.PlayerStats.ContainsKey(key),
                PanelStats.LifeRegenerationMultiplier(get));
        }
    }

    public static void CarryFortification(ClientState conn, uint ticket)
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

    public static async Task RunFortification(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerUpdatesState updates)
    {
        if (!PlayerUpdatesCurrent(conn, updates)) return;
        RestoreFortification(conn);
        while (PlayerUpdatesCurrent(conn, updates))
        {
            await updates.Schedule.Wait(PlayerUpdateRole.Fortification, 32, updates.Stop.Token);
            if (!PlayerUpdatesCurrent(conn, updates)) break;
            try
            {
                await SendFortification(remote, stream, ciphers, conn);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
        }
    }

    public static void RestoreFortification(ClientState conn)
    {
        lock (conn.Monsters)
            if (CarriedFortification.TryRemove(conn.Ticket, out var entry)
                && DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && conn.PlayerLife > 0)
                FortificationOf(conn).Restore(entry.State, conn.Clock.Elapsed.TotalSeconds);
    }

    // Construct buffs, stats and Actor records together inside the send gate from the current owner state.
    // Polled every 32 ms: without Fortification shown and with nothing changed since the last presentation (every gain moves
    // the revision) there is nothing to send, so the send gate and the Monsters lock are left alone.
    public static Task SendFortification(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        FortificationOf(conn) is { Buff: 0 } idle && idle.PresentedRevision == idle.Revision ? Task.CompletedTask : Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            using (conn.Performance.Lock(conn.Monsters, "fortification"))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused) return null;
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
                                (value.NextExpiry!.Value - now) * session.State.ExpiryRate)),
                            stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
                    }
                    // Fortification changes on every melee hit; rebuilding the whole skill list here held the
                    // Monsters lock and the send gate for 50-110 ms (2026-11-05), starving input and the other sends.
                    // Its stacks and attack speed are in the 0x1b3 stats; the server keeps the authoritative action
                    // timings, so the skill records are left to the next gem/equipment refresh.
                    packets.Add(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
                    session.PresentedRevision = session.Revision;
                    return packets.SelectMany(p => p).ToArray();
                });
            }
        });
}
