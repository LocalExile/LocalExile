using System.Collections.Concurrent;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    sealed record RageTransfer(string Character, DateTime At, RageState.Carried State);
    static readonly ConcurrentDictionary<uint, RageTransfer> CarriedRage = new();

    static int RageStat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    // Called after raw character/utility inputs and before attack damage/panel calculations. Derived values are
    // authoritative: setting current_rage cannot create resources or bypass gain prevention.
    static void WithRage(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
        {
            if (conn.RageRetired || conn.PlayerLife == 0) conn.Rage.Clear();
            var inputs = RageStats.Inputs(name => RageStat(stats, name));
            // Stat projections must not advance Rage past an unprocessed projectile contact.
            var value = conn.Rage.Read(ProjectileWorldTime(conn), inputs);
            foreach (var (name, amount) in RageStats.Display(value, inputs))
                if (Stats.Key(name) is uint key) stats[key] = amount;
        }
    }

    // Only accepted contacts call these, in the same Monsters critical section as life mutation, before any await.
    static bool RageOnHitLocked(ClientState conn, bool attack, bool melee, bool critical,
        IReadOnlyDictionary<uint, int>? own = null, Random? random = null)
    {
        if (conn.Character is null || conn.Closed || conn.LeavingArea || conn.RageRetired
            || conn.Paused || conn.PlayerLife == 0) return false;
        var player = CharacterPlayerStats(conn);
        int Get(string name) => (int)Math.Clamp((long)RageStat(player, name)
            + (own is null ? 0 : RageStat(own, name)), int.MinValue, int.MaxValue);
        var inputs = RageStats.Inputs(Get);
        return conn.Rage.Hit(ProjectileWorldTime(conn), inputs, attack, melee, critical, (random ?? Random.Shared).Next(100),
            soulGainPrevention: Get("has_soul_gain_prevention") != 0);
    }

    static void RageWhenHitLocked(ClientState conn)
    {
        if (conn.RageRetired || conn.Closed || conn.LeavingArea || conn.Paused) return;
        if (conn.PlayerLife == 0) { conn.Rage.Clear(); return; }
        if (conn.Character is null) return;
        var stats = CharacterPlayerStats(conn);
        conn.Rage.EnemyHit(ProjectileWorldTime(conn), RageStats.Inputs(name => RageStat(stats, name)),
            Random.Shared.Next(100), soulGainPrevention: RageStat(stats, "has_soul_gain_prevention") != 0);
    }

    internal static void ClearRageOnDeath(ClientState conn)
    {
        lock (conn.Monsters) conn.Rage.Clear();
    }

    // Successful Auth transfer moves count, loss fraction, activity age and shared interval exactly once. Loading time
    // is frozen by policy. The old connection is retired even when empty; it cannot mint a second carried balance.
    internal static void CarryRage(ClientState conn, uint ticket)
    {
        lock (conn.Monsters)
        {
            if (conn.RageRetired || conn.Closed) return;
            var stats = CharacterPlayerStats(conn);
            if (conn.PlayerLife == 0) conn.Rage.Clear();
            var state = conn.Rage.Carry(conn.Clock.Elapsed.TotalSeconds, RageStats.Inputs(name => RageStat(stats, name)));
            conn.RageRetired = true;
            foreach (var (old, entry) in CarriedRage)
                if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedRage.TryRemove(old, out _);
            CarriedRage.TryAdd(ticket, new(CharacterName(conn), DateTime.UtcNow, state));
        }
    }

    internal static void RestoreRage(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.RageRetired || !CarriedRage.TryGetValue(conn.Ticket, out var entry)
                || entry.Character != CharacterName(conn)) return;
            if (!CarriedRage.TryRemove(new KeyValuePair<uint, RageTransfer>(conn.Ticket, entry))) return;
            if (DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && conn.PlayerLife > 0)
                conn.Rage.Restore(entry.State, conn.Clock.Elapsed.TotalSeconds);
        }
    }

    // Executed inside the connection's send gate so a queued refresh cannot resurrect a dead/retired balance or buff.
    internal static byte[]? RagePresentation(ClientState conn, RagePrepared? prepared = null)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
            // Reconcile before all Rage reads/snapshots, even when the Rage value itself has not changed.
            var bytes = WithOwnAuraChanges(conn, () => RageSnapshot(conn, prepared));
            return bytes.Length == 0 ? null : bytes;
        }
    }

    // Called only by RagePresentation under the send gate and state lock; no send or reconciliation recursion.
    static byte[] RageSnapshot(ClientState conn, RagePrepared? prepared = null)
    {
        var stats = CharacterPlayerStats(conn);
        var inputs = RageStats.Inputs(name => RageStat(stats, name));
        var value = conn.Rage.Read(ProjectileWorldTime(conn), inputs);
        if (conn.SentRage == value) return [];
        var bytes = new List<byte>();
        if (conn.RageBuff != 0) bytes.AddRange(InstanceEntities.RemoveBuff(PlayerId, conn.RageBuff));
        conn.RageBuff = 0;
        if (value.Current > 0)
        {
            conn.RageBuff = conn.NextBuff++;
            bytes.AddRange(InstanceEntities.AddBuff(PlayerId, conn.RageBuff, 1060, PlayerId,
                stacks: (ushort)Math.Min(ushort.MaxValue, value.Current)));
        }
        bytes.AddRange(PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)));
        // The skill list prepared before the gate for this very value, else built here (a value that moved meanwhile).
        var skills = prepared is { } p && p.Value == value ? p.Skills : PlayerSkills(conn);
        bytes.AddRange(InstanceEntities.PlayerSkillsChange(PlayerId, skills, []));
        conn.SentRage = value;
        Console.WriteLine($"[gateway] {CharacterName(conn)} Rage {value.Current}/{value.Maximum}, "
            + $"effective {value.Effective}, policy v{RageState.PolicyVersion} "
            + $"(t={conn.Clock.Elapsed.TotalSeconds:0.000})");
        return [.. bytes];
    }

    /// <summary>A Rage value and the skill list built for it ahead of the send gate.</summary>
    internal sealed record RagePrepared(RageState.Value Value, IReadOnlyList<GrantedSkill> Skills);

    // The skill list is the expensive part of a Rage change (every gem's panel damage): build it before the send gate, so
    // the gate, which every hit and echo waits on, holds only the cheap comparison and the packet assembly.
    internal static Task SendRage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        RagePrepared? prepared = null;
        // Neither the stats nor the skill list hold the combat lock for their whole calculation; only the read does.
        var stats = CharacterPlayerStats(conn);
        RageState.Value? changed = null;
        lock (conn.Monsters)
            if (!conn.Closed && !conn.LeavingArea && !conn.RageRetired)
            {
                var value = conn.Rage.Read(conn.Clock.Elapsed.TotalSeconds, RageStats.Inputs(name => RageStat(stats, name)));
                if (conn.SentRage != value) changed = value;
            }
        if (changed is { } next) prepared = new(next, PlayerSkills(conn));
        return Send(remote, stream, ciphers, [], null, atGate: () => RagePresentation(conn, prepared));
    }

    static async Task RunRage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            while (!conn.Closed && !conn.RageRetired)
            {
                await SendRage(remote, stream, ciphers, conn);
                await Task.Delay(100);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
