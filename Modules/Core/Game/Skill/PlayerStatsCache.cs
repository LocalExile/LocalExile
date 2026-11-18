using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

// The player's full stat snapshot is rebuilt by every hit, cast, leech, aura and flask check: within one skill use (C→S 0x24,
// 0x25) it was built several times over with the same inputs (live 2026-11-05: up to 1.3 s per message in the Upper
// Sceptre of God, the input queue 16 s behind, and the move echoes put the player back where they had been). Inside such a
// message's scope (PlayerStatsScope) a snapshot is reused while its inputs are the same: the pools, the buffs added, life,
// the mana's conditions (ManaState), the auras, moving and the /set_stat overrides, for at most Lifetime (a task the message started outlives it).
// Everything else (equipment, item and passive messages, ticks) builds its own.
public static partial class Game
{
    sealed class PlayerStatsEntry
    {
        public double At;
        public object? Pools;
        public long NextBuff, Life, Mana;
        public long Golems;
        public long AspectRevision;
        public long MadnessRevision;
        public long SiphoningRevision;
        public (int Endurance, int Frenzy, int Power) HeldCharges;
        public int IntimidatingCryMovement;
        public long Slayer, Offerings, Hierophant;
        public bool ArcaneSurge;
        public int Overrides, Auras, ResistancePenalty;
        public bool Moving, GolemSummonedRecently;
        // The Energy Shield's conditions (full, recharging) and the enemy hits taken recently (RecentEnemyHits.Counter):
        // found missing by rebuilding on every reuse and comparing (2026-11-18): stale up to a throttle window before.
        public bool EnergyShieldFull, EnergyShieldRecharging;
        public int HitsTakenRecently;
        public StatSourcesResult? StatSources;
        public Dictionary<uint, int>? Stats;
        public double Baseline;
    }

    const double PlayerStatsLifetime = .05;

    sealed class PlayerStatsCacheScope(ClientState conn) : IDisposable
    {
        public readonly ClientState Conn = conn;
        public PlayerStatsEntry? Entry;
        public void Dispose() { if (ReferenceEquals(CurrentPlayerStatsScope.Value, this)) CurrentPlayerStatsScope.Value = null; }
    }
    static readonly AsyncLocal<PlayerStatsCacheScope?> CurrentPlayerStatsScope = new();

    /// <summary>Reuse the player's stat snapshot within this message (see above).</summary>
    public static IDisposable PlayerStatsScope(ClientState conn) => CurrentPlayerStatsScope.Value = new(conn);

    static PlayerStatsEntry PlayerStatsKey(ClientState conn, double now)
    {
        int overrides;
        StatSourcesResult? statSources;
        long siphoningRevision;
        (int Endurance, int Frenzy, int Power) heldCharges;
        int hitsTaken;
        lock (conn.Monsters)
        {
            hitsTaken = conn.RecentEnemyHits.Count(conn.Clock.Elapsed.TotalSeconds);
            overrides = conn.PlayerStats.Count;
            // Reconcile source-owned charges before capturing the source identity or held standard charges.
            siphoningRevision = Game.SiphoningRevision(conn);
            statSources = conn.StatSourcesCache;
            var charges = ChargesOf(conn).State;
            heldCharges = (charges.Held(ChargeType.Endurance), charges.Held(ChargeType.Frenzy), charges.Held(ChargeType.Power));
        }
        return new()
        {
            At = now, Pools = conn.Pools, NextBuff = conn.NextBuff, Life = (long)conn.PlayerLife, Mana = ManaState(conn),
            Overrides = overrides, Auras = conn.Auras.Count, Moving = conn.Walk.Moving(now), Golems = conn.Golems.Version,
            GolemSummonedRecently = GolemSummonedInPastEightSeconds(conn, AreaTime(conn)), StatSources = statSources,
            AspectRevision = conn.Reservations.AspectRevision,
            MadnessRevision = conn.Madness.Revision,
            SiphoningRevision = siphoningRevision,
            HeldCharges = heldCharges,
            IntimidatingCryMovement = Game.IntimidatingCryMovement(conn),
            Slayer = global::Slayer.Revision(conn), Offerings = conn.Summons.OfferingRevision, Hierophant = global::Hierophant.Revision(conn),
            ArcaneSurge = conn.SupportGems.ArcaneSurgeActiveAt(now),
            ResistancePenalty = Game.ResistancePenalty(conn),
            EnergyShieldFull = conn.EnergyShield.Full, EnergyShieldRecharging = conn.EnergyShield.Recharging,
            HitsTakenRecently = hitsTaken,
        };
    }

    // The snapshot reads the current mana only as its conditions (ItemConditionFlags: full, half or less) and empty;
    // the amount itself was the key, so every point of regeneration after a cast missed the cache and each of its
    // readers (charges, recovery, the pose, crit context) built the whole snapshot again under the lock until the pool
    // was full (CPU sample 2026-11-18: 5.5k of 52k samples in a Fireball fight, second only to the monster AI).
    static long ManaState(ClientState conn)
    {
        long mana = conn.PlayerMana, pool = conn.Pools.Mana;
        return (mana >= pool ? 1 : 0) | (mana * 2 <= pool ? 2 : 0) | (mana == 0 ? 4 : 0);
    }

    static bool SameInputs(PlayerStatsEntry a, PlayerStatsEntry b) =>
        b.At - a.At is >= 0 and < PlayerStatsLifetime && SameSources(a, b);

    static bool SameSources(PlayerStatsEntry a, PlayerStatsEntry b) => Equals(a.Pools, b.Pools) && a.NextBuff == b.NextBuff
        && a.Life == b.Life && a.Mana == b.Mana && a.Overrides == b.Overrides && a.Auras == b.Auras && a.Moving == b.Moving
        && a.Golems == b.Golems && a.GolemSummonedRecently == b.GolemSummonedRecently
        && a.AspectRevision == b.AspectRevision
        && a.MadnessRevision == b.MadnessRevision
        && a.SiphoningRevision == b.SiphoningRevision
        && a.HeldCharges == b.HeldCharges
        && a.IntimidatingCryMovement == b.IntimidatingCryMovement
        && a.Offerings == b.Offerings && a.Slayer == b.Slayer && a.Hierophant == b.Hierophant && a.ArcaneSurge == b.ArcaneSurge
        && a.ResistancePenalty == b.ResistancePenalty
        && a.EnergyShieldFull == b.EnergyShieldFull && a.EnergyShieldRecharging == b.EnergyShieldRecharging
        && a.HitsTakenRecently == b.HitsTakenRecently
        && ReferenceEquals(a.StatSources, b.StatSources);

    public static Dictionary<uint, int> CachedPlayerStats(ClientState conn, out double baselineLifePerSecond)
    {
        // In an area's frames: the build of the last Stats phase (StatsSystem.cs), until the next.
        if (FrameStatsOf.TryGetValue(conn, out var frame) && frame.ByFrame && frame.Graph is { } graph)
        {
            baselineLifePerSecond = frame.Outputs.BaselineLifePerSecond;
            return new(graph.Values);
        }
        if (CurrentPlayerStatsScope.Value is not { } scope || !ReferenceEquals(scope.Conn, conn))
            return ThrottledPlayerStats(conn, out baselineLifePerSecond);
        var key = PlayerStatsKey(conn, conn.Clock.Elapsed.TotalSeconds);
        lock (scope)
            if (scope.Entry is { Stats: { } hit } last && SameInputs(last, key))
            {
                baselineLifePerSecond = last.Baseline;
                return new(hit);
            }
        var stats = CharacterPlayerStatsSnapshot(conn, out baselineLifePerSecond, out _);
        key.Stats = new(stats);
        key.Baseline = baselineLifePerSecond;
        lock (scope) scope.Entry = key;
        return stats;
    }

    // A live connection (its player update loops run) rebuilds its snapshot at most once a frame of 60 Hz while the inputs
    // above are the same: a fight asked for 157 full builds a second (live 2026-11-05, 56% of a core), most of them for the
    // same state. Anything slower than a frame (regeneration, Rage decay, Fortification expiry, "recently" windows) is at
    // most that late; a change of the pools, a buff added, life, a mana condition, the auras, moving or an override rebuilds at once.
    // Off a live connection (tests, the build scopes) every read builds.
    const double PlayerStatsFrame = 1.0 / 60;
    sealed class ThrottledStats { public PlayerStatsEntry? Entry; }
    static readonly RuleTable<ClientState, ThrottledStats> ThrottledPlayerStatsEntries = new();

    static Dictionary<uint, int> ThrottledPlayerStats(ClientState conn, out double baselineLifePerSecond)
    {
        if (conn.PlayerUpdatesState is null || conn.Closed)
            return CharacterPlayerStatsSnapshot(conn, out baselineLifePerSecond, out _);
        var holder = ThrottledPlayerStatsEntries.GetOrCreateValue(conn);
        var key = PlayerStatsKey(conn, conn.Clock.Elapsed.TotalSeconds);
        lock (holder)
            if (holder.Entry is { Stats: { } hit } last && key.At - last.At is >= 0 and < PlayerStatsFrame && SameInputs(last, key))
            {
                baselineLifePerSecond = last.Baseline;
                return new(hit);
            }
        var stats = CharacterPlayerStatsSnapshot(conn, out baselineLifePerSecond, out _);
        key.Stats = new(stats);
        key.Baseline = baselineLifePerSecond;
        lock (holder) holder.Entry = key;
        return stats;
    }

    // ---- stats by frame ----

    // A player's stats in an area's frames: a stat graph with a rule per step (PlayerStatSteps.cs), stepped once in the
    // Stats phase; a step runs again when a stat it read changed before it or a change marks it, and reads in between see
    // the last step. The character panel is published there too, once a frame however many changes asked for it.
    sealed class FrameStats
    {
        public bool Dirty = true, ByFrame;
        public StatGraph? Graph;
        public readonly PlayerStepOutputs Outputs = new();
        public PlayerStatsEntry? Key;
        public string? Publish;
        public Task Publishing = Task.CompletedTask;
        public int Steps, Builds, Published;
        // When (connection clock) a timed effect the last build read next changes (StatsChangeIn).
        public double NextChange = double.PositiveInfinity;
    }

    // The earliest change the Stats phase's build being made heard of (StatsChangeIn), in seconds from now.
    [ThreadStatic] static bool collectingChanges;
    [ThreadStatic] static double buildNextChange;

    /// <summary>For what a stat build reads that runs out or changes by itself (a buff's end, a "recently" window): it
    /// changes in <paramref name="seconds"/>, and the player's stats are built again in the Stats phase then.</summary>
    public static void StatsChangeIn(double seconds)
    {
        if (collectingChanges && seconds < buildNextChange) buildNextChange = Math.Max(0, seconds);
    }
    static readonly ConditionalWeakTable<ClientState, FrameStats> FrameStatsOf = new();

    /// <summary>A change of the player's stats: they are built again in the next Stats phase.</summary>
    public static void MarkPlayerStatsDirty(ClientState conn)
    {
        if (FrameStatsOf.TryGetValue(conn, out var frame)) lock (frame) frame.Dirty = true;
    }

    // Asks the Stats phase to publish the panel (SendCharacterStats); false off the frames, which publish at once.
    static bool PublishByFrame(ClientState conn, string why)
    {
        if (!FrameStatsOf.TryGetValue(conn, out var frame) || !frame.ByFrame) return false;
        lock (frame) { frame.Dirty = true; frame.Publish ??= why; }
        return true;
    }

    static readonly bool VerifyFrameStats = Environment.GetEnvironmentVariable("Game_STATS_FRAME_VERIFY") == "1";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FrameStatsMisses = new();

    /// <summary>The player's part of the Stats phase.</summary>
    internal static void StepPlayerStats(ClientState conn)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !conn.PlayerSent
            || conn.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(conn, updates)) return;
        var frame = FrameStatsOf.GetValue(conn, _ => new());
        double clock = conn.Clock.Elapsed.TotalSeconds;
        var key = PlayerStatsKey(conn, clock);
        bool dirty;
        lock (frame) dirty = frame.Dirty || frame.Graph is null || frame.Key is null || !SameSources(frame.Key, key)
            || clock >= frame.NextChange;
        var graph = frame.Graph ??= PlayerStatGraph(conn, frame.Outputs);
        if (dirty)
        {
            lock (frame) { frame.Dirty = false; frame.Key = key; }
            for (int i = 0; i < PlayerSteps.Length; i++) graph.MarkRule(i);
        }
        (collectingChanges, buildNextChange) = (true, double.PositiveInfinity);
        try { graph.Step(); }
        finally
        {
            collectingChanges = false;
            if (dirty) frame.NextChange = clock + buildNextChange;
        }
        if (VerifyFrameStats) VerifyFrame(conn, frame);
        frame.ByFrame = true;
        if (dirty) frame.Builds++;
        if (++frame.Steps == 300)
        {
            // Game_TRACE=budget: how often the phase built and published them (300 frames, about 10 s).
            if (GameServer.Core.Diagnostics.ServerTrace.Budget)
                Console.WriteLine($"[stats] {conn.Wire.Remote} {frame.Builds} build(s), {frame.Published} panel(s) in {frame.Steps} frames");
            frame.Steps = frame.Builds = frame.Published = 0;
        }
        string? why;
        lock (frame)
        {
            if (frame.Publish is null || !frame.Publishing.IsCompleted) return;
            (why, frame.Publish) = (frame.Publish, null);
            frame.Published++;
        }
        var (remote, stream, ciphers) = conn.Wire;
        frame.Publishing = AsPlayer(conn, updates, () => PublishCharacterStats(remote, stream, ciphers, conn, why));
    }

    static StatGraph PlayerStatGraph(ClientState conn, PlayerStepOutputs outputs)
    {
        var graph = new StatGraph();
        foreach (var step in Enum.GetValues<PlayerStatStep>()) graph.AddRule(view => RunPlayerStep(step, conn, view, outputs));
        return graph;
    }

    /// <summary>A change that one step reads: that step runs again in the next Stats phase (and what it changes, after it).</summary>
    public static void MarkPlayerStats(ClientState conn, PlayerStatStep step)
    {
        if (FrameStatsOf.TryGetValue(conn, out var frame) && frame.Graph is { } graph) graph.MarkRule((int)step);
    }

    // Game_STATS_FRAME_VERIFY=1: the graph's values against every step run in order now; what differs is a change no
    // mark reached.
    static void VerifyFrame(ClientState conn, FrameStats frame)
    {
        if (frame.Graph is not { } graph) return;
        var kept = graph.Values;
        var fresh = CharacterPlayerStatsSnapshot(conn, out _, out _);
        var wrong = fresh.Keys.Union(kept.Keys).Where(k => fresh.GetValueOrDefault(k) != kept.GetValueOrDefault(k))
            .Select(k => Stats.Name(k)).Order().ToArray();
        if (wrong.Length == 0) return;
        string names = string.Join(" ", wrong.Take(12));
        int seen = FrameStatsMisses.AddOrUpdate(names, 1, (_, n) => n + 1);
        if (seen is 1 or 10 or 100 or 1000)
            Console.WriteLine($"[stats-verify] {conn.Wire.Remote} {wrong.Length} stat(s) changed unmarked (x{seen}): {names}");
    }

    // ---- the skill list ----

    // The last full skill list (0x2bc, no removals) this client was sent: the Rage, charge, aura, Fortification, Onslaught,
    // Rampage and Antiquarian refreshes resend it whole, about 32 KB, at every change of theirs, also when not one skill
    // changed. An unchanged list is left out. The full refresh (SendCharacterStats) always sends and records it.
    sealed class SentSkillList { public byte[]? Bytes; public IReadOnlyList<GrantedSkill>? Skills; }
    static readonly RuleTable<ClientState, SentSkillList> SentSkillLists = new();

    public static byte[] PlayerSkillsIfChanged(ClientState conn, IReadOnlyList<GrantedSkill> skills)
    {
        var bytes = InstanceEntities.PlayerSkillsChange(PlayerId, skills, [], canDetonateMines: CanDetonateMines(conn));
        var sent = SentSkillLists.GetOrCreateValue(conn);
        lock (sent)
        {
            if (sent.Bytes is { } last && last.AsSpan().SequenceEqual(bytes)) return [];
            (sent.Bytes, sent.Skills) = (bytes, skills);
        }
        return bytes;
    }

    // What the full refresh sent: the base the next partial refresh compares with.
    public static void RecordPlayerSkills(ClientState conn, IReadOnlyList<GrantedSkill> skills)
    {
        var sent = SentSkillLists.GetOrCreateValue(conn);
        lock (sent) (sent.Bytes, sent.Skills) = (InstanceEntities.PlayerSkillsChange(PlayerId, skills, [], canDetonateMines: CanDetonateMines(conn)), skills);
    }

    /// <summary>A stat of the skill as the client last received it in the skill list (0 when the skill went without it),
    /// null when the skill was not sent. The client times some effects by the skill's stats it holds, not the
    /// character's current ones (Hammerfalls' waves).</summary>
    public static int? SentSkillStat(ClientState conn, ushort skillId, string stat)
    {
        if (Stats.Key(stat) is not uint key || !SentSkillLists.TryGetValue(conn, out var sent)) return null;
        lock (sent)
        {
            if (sent.Skills?.FirstOrDefault(s => s.Id == skillId) is not { } skill) return null;
            foreach (var (k, value) in skill.Stats ?? []) if (k == key) return value;
            return 0;
        }
    }
}
