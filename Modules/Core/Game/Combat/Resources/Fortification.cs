using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    public static readonly Store<Player, FortificationSession> FortificationSessions = new();
    public static readonly ConcurrentDictionary<uint, (DateTime At, FortificationState.Transfer State)> CarriedFortification = new();
    public static FortificationSession FortificationOf(Player conn) =>
        FortificationSessions.GetValue(conn, c => new()
        {
            Changed = () => MarkPlayerStats(c, PlayerStatStep.TimedRecovery),
        });

    public static void EnsureFortificationPools(Player conn)
    {
        lock (conn.Monsters)
        {
            var session = FortificationOf(conn);
            session.Read(AreaTime(conn));
            if (conn.Pools.FortificationRevision != session.Revision) conn.Pools = PoolsFor(conn, conn.Pools.Level);
        }
    }

    public static decimal RawFortificationDamage(DamagePacket rolled, bool critical, int multiplier)
    {
        decimal total = rolled.Components.Sum(c => (decimal)c.Range.Min);
        return Math.Max(0, total) * (critical ? Math.Max(0, multiplier) / 100m : 1m);
    }

    public static Dictionary<uint, int> WithFortification(Player conn, Dictionary<uint, int> sources)
    {
        lock (conn.Monsters)
        {
            int Get(uint key) => sources.GetValueOrDefault(key);
            var session = FortificationOf(conn);
            if (conn.PlayerLife == 0 && session.Value.Uncapped > 0) session.Clear();
            var value = session.Observe(AreaTime(conn), Get);
            void Add(uint name, int added)
            {
                if (name is var key)
                    sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + added, int.MinValue, int.MaxValue);
            }
            Add(FortificationSession.AttackSpeed, value.IncreasedAttackSpeedPercent(session.SpeedPerStack));
            AddFortificationCounts(new PlainStatMap(sources), value);
            return sources;
        }
    }

    public static void AddFortificationCounts(StatMap stats, FortificationState.Value value)
    {
        foreach (var (name, amount) in new[] { (FortificationSession.Current, value.Current),
            (FortificationSession.Uncapped, (int)Math.Min(int.MaxValue, value.Uncapped)),
            (FortificationSession.Maximum, value.Maximum), (Stat.HasFortify, value.Current > 0 ? 1 : 0),
            (Stat.HasFortification, value.Current > 0 ? 1 : 0) })
            if (name is var key) stats[key] = amount;
    }

    // Called only at an accepted melee damage commit, before any notification can yield to another action/area.
    public static bool FortificationOnHitLocked(Player conn, SpawnedMonster target, decimal rawDamage,
        bool melee, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill = null, Random? random = null)
    {
        if (!melee || !target.Hostile || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
        int Get(uint name)
        {
            if (!(name is var key)) return 0;
            return (int)Math.Clamp((long)player.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0),
                int.MinValue, int.MaxValue);
        }
        return FortificationOf(conn).Hit(AreaTime(conn), rawDamage, target.MaxLife,
            target.Rarity, Get, random ?? AreaRng(conn));
    }

    /// <summary>A successful melee stun (HitReaction.TryFlinch): roll the character's chance to Fortify and grant the
    /// hit's damage-based stacks. Called under the owner's Monsters lock.</summary>
    internal static bool FortificationFromMeleeStun(Player conn, decimal rawDamage, decimal maximumLife,
        MonsterRarity rarity, Random? random = null)
    {
        if (conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
        var player = CharacterPlayerStats(conn);
        int Get(uint key) => player.GetValueOrDefault(key);
        return FortificationOf(conn).FortifyFromStun(AreaTime(conn), rawDamage, maximumLife, rarity,
            Get, random ?? AreaRng(conn));
    }

    public static int FortificationDamageTaken(Player conn, int damage)
    {
        lock (conn.Monsters)
        {
            var value = FortificationOf(conn).Read(AreaTime(conn));
            return (int)((long)Math.Max(0, damage) * (100 - Math.Min(100, value.Current)) / 100);
        }
    }

    public static void ConfigureFortificationRecovery(Player conn, uint maximumLife,
        IReadOnlyDictionary<uint, int> sources)
    {
        var recovery = new Dictionary<uint, int>(sources);
        AddItemUtilityFlaskRawStats(new PlainStatMap(recovery),
            conn.UtilityFlaskEffects.Stats().Concat(conn.FlaskGrounds.PlayerStats()), Stats);
        ConfigureFortificationRecovery(conn, maximumLife,
            name => name is var key ? recovery.GetValueOrDefault(key) : 0);
    }

    public static void ConfigureFortificationRecovery(Player conn, uint maximumLife, Func<uint, int> get)
    {
        lock (conn.Monsters)
        {
            FortificationOf(conn).ConfigureRecovery(AreaTime(conn), maximumLife,
                false,
                PanelStats.LifeRegenerationMultiplier(get));
        }
    }

    public static void CarryFortification(Player conn, uint ticket)
    {
        lock (conn.Monsters)
        {
            var session = FortificationOf(conn);
            if (conn.PlayerLife > 0)
            {
                var transfer = session.State.Carry(AreaTime(conn));
                if (transfer.Grants.Count > 0) CarriedFortification[ticket] = (DateTime.UtcNow, transfer);
            }
            session.Clear();
        }
        foreach (var (old, entry) in CarriedFortification)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedFortification.TryRemove(old, out _);
    }

    public static void RestoreFortification(Player conn)
    {
        lock (conn.Monsters)
            if (CarriedFortification.TryRemove(conn.Ticket, out var entry)
                && DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && conn.PlayerLife > 0)
                FortificationOf(conn).Restore(entry.State, AreaTime(conn));
    }

    // Construct buffs, stats and Actor records together inside the send gate from the current owner state. A stat
    // presentation: without Fortification shown and with nothing changed since the last presentation (every gain moves
    // the revision) there is nothing to send, so the send gate and the Monsters lock are left alone.
    public static Task SendFortification(Player conn) =>
        FortificationOf(conn) is { Buff: 0 } idle && idle.PresentedRevision == idle.Revision ? Task.CompletedTask : Send(conn, [], null, atGate: () =>
        {
            using (conn.Performance.Lock(conn.Monsters, "fortification"))
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused) return null;
                var session = FortificationOf(conn);
                double now = AreaTime(conn);
                using var snapshot = session.Capture(now);
                if (conn.PlayerLife == 0) { if (session.Value.Uncapped > 0) session.Clear(); }
                session.Read(AreaTime(conn));
                if (session.PresentedRevision == session.Revision) return null;
                ReconcileOwnAuras(conn);
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                return WithOwnAuraChanges(conn, () =>
                {
                    // Build and acknowledge the tail AFTER any aura prefix, in the same order as its wire packets.
                    var value = session.Read(AreaTime(conn));
                    var packets = new List<byte[]>();
                    if (session.Buff != 0) packets.Add(InstanceEntities.RemoveBuff(PlayerId, session.Buff));
                    session.Buff = 0;
                    if (value.Current > 0)
                    {
                        session.Buff = conn.NextBuff++;
                        packets.Add(InstanceEntities.AddBuff(PlayerId, session.Buff, 1943, PlayerId,
                            visual: BuffArtVisual(conn, "fortify_buff", Stat.VirtualFortifyArtVariation),
                            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0,
                                (value.NextExpiry!.Value - now) * session.State.ExpiryRate)),
                            stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
                    }
                    // Fortification changes on every melee hit; rebuilding the whole skill list here held the
                    // Monsters lock and the send gate for 50-110 ms (2026-11-05), starving input and the other sends.
                    // Its stacks and attack speed are in the 0x1b3 stats; the server keeps the authoritative action
                    // timings, so the skill records are left to the next gem/equipment refresh.
                    packets.Add(PlayerStatsOffFrames(conn));
                    session.PresentedRevision = session.Revision;
                    return packets.SelectMany(p => p).ToArray();
                });
            }
        });
}
