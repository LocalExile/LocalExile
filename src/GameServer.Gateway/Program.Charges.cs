using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Connection ownership prevents a stale instance from mutating a new connection's charges. Only an explicit,
    // destination-ticket-keyed transfer may cross connections; a normal logout/login starts a fresh session.
    sealed class ChargeRuntime
    {
        public ChargeSession Session { get; } = new();
        public Dictionary<ChargeType, ushort> Buffs { get; } = [];
        public Dictionary<uint, int> AppliedEffects { get; set; } = [];
        public long Revision, PublishedRevision;
        public long PreparedRevision = long.MinValue;
        public CharacterStats.Pools? PreparedPools;
        public bool Dead, Observed;
        public int LoopStarted;
    }

    static readonly ConditionalWeakTable<ClientState, ChargeRuntime> ChargeRuntimes = new();
    sealed record ChargeCarry(ChargeSession.Transfer Transfer, DateTimeOffset DiscardAfter);
    static readonly ConcurrentDictionary<uint, ChargeCarry> CarriedCharges = new();
    static readonly TimeSpan ChargeTransferLease = TimeSpan.FromMinutes(10);

    static ChargeRuntime ChargesOf(ClientState conn) => ChargeRuntimes.GetValue(conn, _ => new());
    static int ChargeValue(IReadOnlyDictionary<uint, int> sources, string name) =>
        Stats.Key(name) is uint key ? sources.GetValueOrDefault(key) : 0;

    static int EffectiveCharge(ClientState conn, ChargeRuntime runtime, ChargeType type) =>
        conn.PlayerLife > 0 && !runtime.Dead ? runtime.Session.Effective(type) : 0;

    static ChargeSession.StepResult StepChargesLocked(ClientState conn, ChargeRuntime runtime, double now, Random rng)
    {
        bool dead = conn.PlayerLife == 0;
        if (runtime.Dead != dead) { runtime.Dead = dead; runtime.Revision++; }
        var result = runtime.Session.Step(now, alive: !dead, moving: now < conn.Walk.ArrivesAt, InTown(conn), rng);
        if (result.CountsChanged) runtime.Revision++;
        return result;
    }

    /// <summary>
    /// Adds inherent charge inputs and the current charge contribution to a fresh raw source vector exactly once.
    /// Call at the end of CharacterStatSources, after equipment conditions, before PassiveBonus. Developer raw-input
    /// replacements apply to their complete input, like ordinary passives/auras. Never pass an already-expanded map.
    /// </summary>
    internal static void ChargeStats(ClientState conn, Dictionary<uint, int> sources)
    {
        lock (conn.Monsters)
        {
            var runtime = ChargesOf(conn);
            if (runtime.Session.Closed) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            // Settle the previous interval before observing a source gained/refunded at this boundary.
            StepChargesLocked(conn, runtime, now, Random.Shared);
            foreach (var (name, amount) in ChargeEffects.Inherent)
                if (Stats.Key(name) is uint key)
                    sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
            int Get(string name) => Stats.Key(name) is uint key
                ? conn.PlayerStats.TryGetValue(key, out int value) ? value : sources.GetValueOrDefault(key) : 0;
            runtime.Session.Observe(Get);
            runtime.Observed = true;
            StepChargesLocked(conn, runtime, now, Random.Shared); // clamp changed limits/minimums at the same boundary
            int Held(ChargeType type) => EffectiveCharge(conn, runtime, type);
            var effects = new Dictionary<uint, int>();
            foreach (var (name, amount) in ChargeEffects.Expand(Get, Held))
                if (Stats.Key(name) is uint key)
                {
                    effects[key] = amount;
                    sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
                }
            foreach (var (name, amount) in ChargeEffects.Display(Held, runtime.Session.Limits))
                if (Stats.Key(name) is uint key) sources[key] = effects[key] = amount;
            // A per-charge input can change without changing the count (e.g. a refund or a developer override).
            // The ticker must still rebuild pools; waiting for the next gain/expiry would retain stale crit/speed.
            if (effects.Count != runtime.AppliedEffects.Count
                || effects.Any(kv => !runtime.AppliedEffects.TryGetValue(kv.Key, out int old) || old != kv.Value))
            {
                runtime.AppliedEffects = effects;
                runtime.Revision++;
            }
        }
    }

    /// <summary>One successful-hit identity shared by every target/projectile belonging to one skill use.</summary>
    internal sealed class ChargeUse
    {
        internal bool HasAcceptedHit;
    }

    /// <summary>
    /// Records only a post-commit player hit, and the same commit's kill. A boss transition uses the actual committed
    /// Life result, not damage compared with pre-hit life. Do not enqueue this kill again from async MonsterKilled.
    /// The first accepted target consumes the once-per-use Frenzy grant; misses/dead/stale targets never reach here.
    /// </summary>
    internal static bool ChargeHitCommitted(ClientState conn, SpawnedMonster target, MonsterHit applied, bool critical,
        bool spell, bool melee, ChargeUse use, IReadOnlyDictionary<uint, int> playerAtHit,
        string? skillEffect = null, IReadOnlyDictionary<uint, int>? skillAtHit = null, int? epoch = null)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0 || target.Owned || !target.Hostile
                || epoch is int expected && conn.ProjectileEpoch != expected) return false;
            var runtime = ChargesOf(conn);
            double at = conn.Clock.Elapsed.TotalSeconds;
            int Get(string name) => ChargeValue(playerAtHit, name);
            int Skill(string name) => skillAtHit is null ? 0 : ChargeValue(skillAtHit, name);
            var weapon = WeaponProfileFor(conn, activeSet: true);
            if (!runtime.Session.EnqueueAccepted(new ChargeEvent.Hit(critical, spell, melee, target.Rarity,
                skillEffect, Skill, applied.Life == 0, Primary: !use.HasAcceptedHit), at, Get, weapon)) return false;
            use.HasAcceptedHit = true;
            if (applied.Life == 0)
                runtime.Session.EnqueueAccepted(new ChargeEvent.Kill(target.Rarity), at, Get, weapon);
            return true;
        }
    }

    /// <summary>For player-owned non-hit kills only (e.g. RF); owned creature kills never grant owner on-kill charges.</summary>
    internal static bool ChargeKillCommitted(ClientState conn, SpawnedMonster target, bool playerKill,
        IReadOnlyDictionary<uint, int> playerAtKill)
    {
        lock (conn.Monsters)
        {
            if (!playerKill || target.Life != 0 || target.Owned || !target.Hostile || conn.Closed || conn.LeavingArea || conn.Paused
                || conn.PlayerLife == 0) return false;
            return ChargesOf(conn).Session.EnqueueAccepted(new ChargeEvent.Kill(target.Rarity),
                conn.Clock.Elapsed.TotalSeconds, name => ChargeValue(playerAtKill, name),
                WeaponProfileFor(conn, activeSet: true));
        }
    }

    /// <summary>Call after the actual incoming-hit/block life mutation; lethal hits cannot leave new charges.</summary>
    internal static bool ChargeIncomingCommitted(ClientState conn, bool blocked, bool spell,
        IReadOnlyDictionary<uint, int> playerAtHit)
    {
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) { ChargesDied(conn); return false; }
            if (conn.Closed || conn.LeavingArea || conn.Paused) return false;
            return ChargesOf(conn).Session.EnqueueAccepted(blocked ? new ChargeEvent.Block(spell) : new ChargeEvent.WhenHit(),
                conn.Clock.Elapsed.TotalSeconds, name => ChargeValue(playerAtHit, name),
                WeaponProfileFor(conn, activeSet: true));
        }
    }

    internal static bool GrantCharges(ClientState conn, ChargeType type, int count, string why,
        IReadOnlyDictionary<uint, int> playerAtGrant)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
            return ChargesOf(conn).Session.EnqueueAccepted(new ChargeEvent.Grant(type, count, why),
                conn.Clock.Elapsed.TotalSeconds, name => ChargeValue(playerAtGrant, name),
                WeaponProfileFor(conn, activeSet: true));
        }
    }

    internal static void ChargesDied(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var runtime = ChargesOf(conn);
            if (runtime.Session.Die(conn.Clock.Elapsed.TotalSeconds) || !runtime.Dead) runtime.Revision++;
            runtime.Dead = true;
        }
    }

    // Packet changes are chosen inside the cipher gate, never retained across a paused/transfer/death interval.
    internal static List<ChargeSession.BuffChange> ChargePresentation(ClientState conn, bool resend = false)
    {
        lock (conn.Monsters)
        {
            var runtime = ChargesOf(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            StepChargesLocked(conn, runtime, now, Random.Shared);
            return ChargePresentationLocked(conn, runtime, now, resend);
        }
    }

    static List<ChargeSession.BuffChange> ChargePresentationLocked(ClientState conn, ChargeRuntime runtime,
        double now, bool resend)
    {
        if (conn.PlayerLife == 0 || runtime.Dead)
        {
            var removed = runtime.Buffs.Select(kv => new ChargeSession.BuffChange(kv.Key, kv.Value, 0, 0, null)).ToList();
            runtime.Buffs.Clear();
            runtime.Session.ForgetShown();
            return removed;
        }
        var changes = runtime.Session.Presentation(now, conn.AllocateBuff, resend);
        foreach (var c in changes)
            if (c.Add == 0) runtime.Buffs.Remove(c.Type);
            else runtime.Buffs[c.Type] = c.Add;
        return changes;
    }

    /// <summary>A prepared pool revision; every state-dependent send revalidates it inside the cipher gate.</summary>
    internal sealed record ChargePublication(long Revision, CharacterStats.Pools Pools, int Epoch);

    /// <summary>
    /// The common character/skill sender must return true only after its last packet was accepted under
    /// ChargePublicationCurrent. It owns normal skill/aura bookkeeping; the charge adapter does not duplicate it.
    /// </summary>
    internal delegate Task<bool> ChargeStatPublisher(ChargePublication publication);

    static bool ChargeConnectionActive(ClientState conn, ChargeRuntime runtime) =>
        !conn.Closed && !conn.LeavingArea && !conn.Paused && !runtime.Session.Closed;

    /// <summary>
    /// Source/catalog and pool construction run outside the combat lock. A changed pool reference or charge revision
    /// retries rather than assigning a result computed against an older state.
    /// </summary>
    internal static ChargePublication? PrepareChargePublication(ClientState conn,
        Func<ClientState, Dictionary<uint, int>>? sources = null,
        Func<ClientState, int, CharacterStats.Pools>? pools = null)
    {
        sources ??= c => CharacterStatSources(c);
        pools ??= PoolsFor;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            lock (conn.Monsters)
                if (!ChargeConnectionActive(conn, ChargesOf(conn))) return null;
            _ = sources(conn); // ChargeStats takes only its own state lock; equipment/passive work has no outer lock
            CharacterStats.Pools before;
            long revision;
            bool prepare;
            lock (conn.Monsters)
            {
                var runtime = ChargesOf(conn);
                if (!ChargeConnectionActive(conn, runtime)) return null;
                before = conn.Pools;
                revision = runtime.Revision;
                prepare = revision != runtime.PreparedRevision || !ReferenceEquals(before, runtime.PreparedPools);
            }
            var rebuilt = prepare ? pools(conn, before.Level) : before;
            lock (conn.Monsters)
            {
                var runtime = ChargesOf(conn);
                if (!ChargeConnectionActive(conn, runtime)) return null;
                if (!ReferenceEquals(conn.Pools, before) || runtime.Revision != revision) continue;
                conn.Pools = rebuilt;
                runtime.PreparedPools = rebuilt;
                runtime.PreparedRevision = revision;
            }
            // This derives CharacterPlayerStats and can read catalogs too; do not wrap it in Monsters.
            double speed = PlayerMovementSpeed(conn);
            lock (conn.Monsters)
            {
                var runtime = ChargesOf(conn);
                if (!ChargeConnectionActive(conn, runtime)) return null;
                if (!ReferenceEquals(conn.Pools, rebuilt) || runtime.Revision != revision) continue;
                conn.Walk.SetSpeed(speed, conn.Clock.Elapsed.TotalSeconds);
                return new(revision, rebuilt, conn.ProjectileEpoch);
            }
        }
        return null; // a busy concurrent rebuild remains dirty for the next ticker; no stale packet is published
    }

    static bool ChargePublicationCurrentLocked(ClientState conn, ChargeRuntime runtime,
        ChargePublication publication, double at)
    {
        if (!ChargeConnectionActive(conn, runtime) || conn.ProjectileEpoch != publication.Epoch) return false;
        // The gate may have waited past an expiry/periodic gain. Settle at this actual publication boundary before
        // comparing revisions, so a zero-duration stale stack and its old pools cannot slip through.
        StepChargesLocked(conn, runtime, at, Random.Shared);
        return runtime.Revision == publication.Revision && runtime.PreparedRevision == publication.Revision
            && ReferenceEquals(conn.Pools, publication.Pools) && ReferenceEquals(runtime.PreparedPools, publication.Pools);
    }

    internal static bool ChargePublicationCurrent(ClientState conn, ChargePublication publication, double? at = null)
    {
        lock (conn.Monsters)
            return ChargePublicationCurrentLocked(conn, ChargesOf(conn), publication, at ?? conn.Clock.Elapsed.TotalSeconds);
    }

    /// <summary>Build only under the cipher gate. Null means stale; an empty array is a valid unchanged presentation.</summary>
    internal static byte[]? ChargeBuffPublication(ClientState conn, ChargePublication publication, bool resend = false,
        double? at = null)
    {
        lock (conn.Monsters)
        {
            var runtime = ChargesOf(conn);
            double now = at ?? conn.Clock.Elapsed.TotalSeconds;
            if (!ChargePublicationCurrentLocked(conn, runtime, publication, now)) return null;
            return ChargePresentationLocked(conn, runtime, now, resend).SelectMany(c => ChargeBuffs.Packets(c, PlayerId))
                .SelectMany(p => p.Packet).ToArray();
        }
    }

    internal static bool CompleteChargePublication(ClientState conn, ChargePublication publication, bool accepted)
    {
        if (!accepted) return false;
        lock (conn.Monsters)
        {
            var runtime = ChargesOf(conn);
            if (!ChargePublicationCurrentLocked(conn, runtime, publication, conn.Clock.Elapsed.TotalSeconds)) return false;
            runtime.PublishedRevision = Math.Max(runtime.PublishedRevision, publication.Revision);
            return true;
        }
    }

    static async Task RefreshCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ChargeStatPublisher publishStats, bool resend = false)
    {
        var publication = PrepareChargePublication(conn);
        if (publication is null) return;
        bool current = false;
        await SendGenerated(remote, stream, ciphers, () =>
        {
            var packets = ChargeBuffPublication(conn, publication, resend);
            current = packets is not null;
            return packets is { Length: > 0 } ? packets : null;
        }, "standard charge buff changes");
        if (!current) return; // leave the revision dirty; a fresh pool build will retry after expiry/gain/state changes
        bool changed;
        lock (conn.Monsters) changed = publication.Revision != ChargesOf(conn).PublishedRevision;
        if (changed || resend)
        {
            bool accepted = await publishStats(publication);
            CompleteChargePublication(conn, publication, accepted);
        }
    }

    static async Task RunCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ChargeStatPublisher publishStats)
    {
        if (Interlocked.Exchange(ref ChargesOf(conn).LoopStarted, 1) != 0) return;
        while (!conn.Closed && !ChargesOf(conn).Session.Closed)
        {
            await Task.Delay(TimeSpan.FromSeconds(ChargeSession.StepSeconds));
            if (conn.Closed || ChargesOf(conn).Session.Closed) break;
            if (conn.LeavingArea) continue; // Auth may reject this transfer; its still-live source loop must resume
            await RefreshCharges(remote, stream, ciphers, conn, publishStats);
        }
    }

    static void PruneChargeTransfers(DateTimeOffset utcNow)
    {
        foreach (var entry in CarriedCharges)
            if (entry.Value.DiscardAfter <= utcNow)
                ((ICollection<KeyValuePair<uint, ChargeCarry>>)CarriedCharges).Remove(entry);
    }

    /// <summary>Call only after Auth accepted an area transfer, using its exact destination ticket.</summary>
    internal static void CarryCharges(ClientState conn, uint destinationTicket, DateTimeOffset utcNow)
    {
        if (destinationTicket == 0) throw new ArgumentOutOfRangeException(nameof(destinationTicket));
        lock (conn.Monsters)
        {
            var runtime = ChargesOf(conn);
            if (runtime.Session.Closed) return;
            // LeavingArea intentionally blocks new events/ticks; committed events queued before it still flush here.
            var carry = runtime.Session.CloseAndCarry(conn.Clock.Elapsed.TotalSeconds, utcNow,
                conn.PlayerLife > 0, conn.Clock.Elapsed.TotalSeconds < conn.Walk.ArrivesAt, InTown(conn), Random.Shared);
            PruneChargeTransfers(utcNow);
            if (!CarriedCharges.TryAdd(destinationTicket, new(carry, utcNow + ChargeTransferLease)))
                throw new InvalidOperationException("A charge transfer already owns this destination ticket");
        }
    }

    /// <summary>Restore once after the destination has its initial raw sources/limits and life, before starting ticks.</summary>
    internal static bool RestoreCharges(ClientState conn, DateTimeOffset utcNow)
    {
        PruneChargeTransfers(utcNow);
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return false;
            var runtime = ChargesOf(conn);
            if (!runtime.Observed || runtime.Session.Closed
                || !CarriedCharges.TryRemove(conn.Ticket, out var carried) || carried.DiscardAfter <= utcNow) return false;
            if (!runtime.Session.Restore(carried.Transfer, conn.Clock.Elapsed.TotalSeconds, utcNow)) return false;
            runtime.Dead = false;
            runtime.Revision++;
            return true;
        }
    }

    /// <summary>A non-transfer disconnect closes its private session and discards gains; it never creates carry.</summary>
    internal static void CloseCharges(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var runtime = ChargesOf(conn);
            if (!runtime.Session.Closed)
                _ = runtime.Session.CloseAndCarry(conn.Clock.Elapsed.TotalSeconds, DateTimeOffset.UtcNow,
                    conn.PlayerLife > 0, conn.Clock.Elapsed.TotalSeconds < conn.Walk.ArrivesAt, InTown(conn), Random.Shared);
        }
    }
}
