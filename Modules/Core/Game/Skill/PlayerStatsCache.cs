using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

// The player's stats. In an area's frames they are the player's stat graph, stepped once a frame in the Stats phase (below):
// a read between two steps sees the last one. Off the frames (tests, tools) a read builds them in full.
public static partial class Game
{
    // What the stats read of mana is its conditions (ItemConditionFlags: full, half or less) and empty: a change of the
    // amount alone marks nothing (CheckConditions).
    static long ManaState(ClientState conn)
    {
        long mana = conn.PlayerMana, pool = conn.Pools.Mana;
        return (mana >= pool ? 1 : 0) | (mana * 2 <= pool ? 2 : 0) | (mana == 0 ? 4 : 0);
    }

    public static Dictionary<uint, int> CachedPlayerStats(ClientState conn, out double baselineLifePerSecond)
    {
        if (FrameStatsOf.TryGetValue(conn, out var frame) && frame.ByFrame && frame.Graph is { } graph)
        {
            baselineLifePerSecond = frame.Outputs.BaselineLifePerSecond;
            return new(graph.Values);
        }
        return CharacterPlayerStatsSnapshot(conn, out baselineLifePerSecond, out _);
    }

    // ---- stats by frame ----

    // A player's stats in an area's frames: a stat graph with a rule per step (PlayerStatSteps.cs), stepped once in the
    // Stats phase; a step runs again when a stat it read changed before it or a change marks it, and reads in between see
    // the last step. The character panel is published there too, once a frame however many changes asked for it.
    sealed class FrameStats
    {
        public bool ByFrame;
        public StatGraph? Graph;
        public readonly PlayerStepOutputs Outputs = new();
        public PlayerStatChange? Publish;
        public Task Publishing = Task.CompletedTask;
        public int Steps, Builds, Published;
        // The steps whose timed effect changes by itself, and when (connection clock): StatsChangeIn.
        // Now: the clock the time is on (StatsChangeAt); null for the connection's.
        public readonly Dictionary<PlayerStatStep, (double At, Func<double>? Now)> Timed = [];
        // The conditions of life, mana and moving the steps read, as last marked; when moving next changes by itself.
        public long Conditions = -1;
        public double MovingCheckAt = double.PositiveInfinity;
        public double Clock;
        // The area the steps last ran in: a new one has none of the last one's stat buffs.
        public GameArea? Area;
        // The items the steps last saw, and their change count (StatInput.Items).
        public PlayerInventories? Items;
        public long ItemChanges;
    }

    /// <summary>The area's stat buffs on a player's entity changed (AreaStatBuffs): its pools and sources are built again.</summary>
    internal static void PlayerStatBuffsChanged(ClientState conn)
    {
        if (!FrameStatsOf.TryGetValue(conn, out var frame) || frame.Graph is not { } graph) return;
        frame.Outputs.RebuildPools = true;
        graph.MarkRule((int)PlayerStatStep.Pools);
        graph.MarkRule((int)PlayerStatStep.Sources);
        // The client's panel and skills follow them, once for the frame.
        if (frame.ByFrame) lock (frame) frame.Publish ??= PlayerStatChange.Sources;
    }

    // The step being run and its frame, for StatsChangeIn.
    [ThreadStatic] static FrameStats? runningFrame;
    [ThreadStatic] static PlayerStatStep runningStep;

    /// <summary>For what a stat step reads that runs out or changes by itself (a buff's end, a "recently" window): it
    /// changes in <paramref name="seconds"/>, and the step runs again in the Stats phase then.</summary>
    public static void StatsChangeIn(double seconds)
    {
        if (runningFrame is not { } frame) return;
        double at = frame.Clock + Math.Max(0, seconds);
        lock (frame.Timed)
            if (!frame.Timed.TryGetValue(runningStep, out var was) || was.Now is not null || at < was.At)
                frame.Timed[runningStep] = (at, null);
    }

    /// <summary>As <see cref="StatsChangeIn"/>, for an effect timed on another clock than the connection's (the
    /// projectile runtime's): the step runs again once <paramref name="now"/> reaches <paramref name="at"/>.</summary>
    public static void StatsChangeAt(double at, Func<double> now)
    {
        if (runningFrame is not { } frame) return;
        lock (frame.Timed) frame.Timed[runningStep] = (at, now);
    }
    static readonly ConditionalWeakTable<ClientState, FrameStats> FrameStatsOf = new();

    // The steps each change marks (PlayerStatChange).
    static readonly PlayerStatStep[][] PlayerStatChangeReaders = BuildPlayerStatChangeReaders();

    static PlayerStatStep[][] BuildPlayerStatChangeReaders()
    {
        var readers = new PlayerStatStep[Enum.GetValues<PlayerStatChange>().Length][];
        // The items, passives and pools are tracked inputs (MarkBuild); these read the rest of a build.
        readers[(int)PlayerStatChange.Build] = [PlayerStatStep.Sources, PlayerStatStep.Subscribers];
        readers[(int)PlayerStatChange.Sources] = [PlayerStatStep.Sources];
        readers[(int)PlayerStatChange.Auras] =
        [
            PlayerStatStep.Pools, PlayerStatStep.Sources, PlayerStatStep.Auras, PlayerStatStep.AuraPenetration,
            PlayerStatStep.CatAspect, PlayerStatStep.AvianCrab, PlayerStatStep.SelfVulnerability, PlayerStatStep.VaalAura,
            PlayerStatStep.SpiritLeadership, PlayerStatStep.TotemAura, PlayerStatStep.AuraRestriction,
        ];
        readers[(int)PlayerStatChange.Flasks] =
            [PlayerStatStep.Sources, PlayerStatStep.UtilityFlasks, PlayerStatStep.UsingFlask, PlayerStatStep.AuraRestriction];
        readers[(int)PlayerStatChange.Minions] = [PlayerStatStep.Sources, PlayerStatStep.Golems, PlayerStatStep.Offerings];
        readers[(int)PlayerStatChange.Leech] =
            [PlayerStatStep.Sources, PlayerStatStep.AttackSpeed, PlayerStatStep.Recovery, PlayerStatStep.LeechingDefences];
        readers[(int)PlayerStatChange.ElementalAilments] =
            [PlayerStatStep.Sources, PlayerStatStep.ElementalAilments, PlayerStatStep.TailCritical];
        readers[(int)PlayerStatChange.IncomingAilments] = [PlayerStatStep.Sources, PlayerStatStep.IncomingAilments];
        readers[(int)PlayerStatChange.God] = [PlayerStatStep.Sources, PlayerStatStep.God];
        readers[(int)PlayerStatChange.Panel] = [];
        return readers;
    }

    // What a build change changes that the steps read as tracked inputs: whatever step read them runs again.
    static readonly StatInput[] BuildInputs = [StatInput.Items, StatInput.Passives, StatInput.Pools];

    // In an area's frames: marks the steps that read the change and asks the Stats phase to publish the panel
    // (SendCharacterStats); false off the frames, which publish at once.
    static bool PublishByFrame(ClientState conn, PlayerStatChange change)
    {
        if (!FrameStatsOf.TryGetValue(conn, out var frame) || !frame.ByFrame || frame.Graph is not { } graph) return false;
        foreach (var step in PlayerStatChangeReaders[(int)change]) graph.MarkRule((int)step);
        if (change == PlayerStatChange.Build)
            foreach (var input in BuildInputs) graph.MarkInput((int)input);
        lock (frame) frame.Publish ??= change;
        return true;
    }

    // What reads the pools, and what reads the conditions of life, mana, Energy Shield and moving (the sources' "while"
    // conditions, the conditional pools, recovery and leech, the tail's low life and recharge rows).
    static readonly PlayerStatStep[] ConditionReaders =
    [
        PlayerStatStep.Pools, PlayerStatStep.Sources, PlayerStatStep.VaalPrevention, PlayerStatStep.Recovery,
        PlayerStatStep.TailPanels, PlayerStatStep.TailOffence, PlayerStatStep.TailCritical,
    ];

    /// <summary>The framework's hand-over (ClientState.StatsChanged): marks the steps that read the input.</summary>
    internal static void PlayerStatInputChanged(ClientState conn, StatInput input)
    {
        if (!FrameStatsOf.TryGetValue(conn, out var frame) || frame.Graph is not { } graph) return;
        switch (input)
        {
            case StatInput.Pools or StatInput.Items or StatInput.Passives:
                graph.MarkInput((int)input);
                break;
            case StatInput.EnergyShield:
                foreach (var step in ConditionReaders) graph.MarkRule((int)step);
                break;
            case StatInput.VaalPrevention:
                graph.MarkRule((int)PlayerStatStep.VaalPrevention);
                break;
            case StatInput.RecentEnemyHits:
                graph.MarkRule((int)PlayerStatStep.RecentHits);
                break;
            case StatInput.FlaskEffects:
                foreach (var step in PlayerStatChangeReaders[(int)PlayerStatChange.Flasks]) graph.MarkRule((int)step);
                break;
            case StatInput.RecentEvents:
                graph.MarkRule((int)PlayerStatStep.Sources);
                break;
            case StatInput.Walk:
                double clock = conn.Clock.Elapsed.TotalSeconds;
                frame.MovingCheckAt = conn.Walk.MovingChangesIn(clock) is double next ? clock + next : double.PositiveInfinity;
                CheckConditions(conn, frame, graph);
                break;
            default:
                CheckConditions(conn, frame, graph);
                break;
        }
    }

    // Life, mana and moving change often (regeneration, every step of a walk); what the steps read of them is a few
    // conditions, and only a change of one marks them.
    static void CheckConditions(ClientState conn, FrameStats frame, StatGraph graph)
    {
        var stats = graph.Values;
        long conditions = ManaState(conn)
            | (conn.PlayerLife == 0 ? 8L : 0) | (CriticalOnLowLife(conn, stats) ? 16L : 0)
            | (CountsAsFullLife(conn.PlayerLife, conn.PlayerMaxLife, stats) ? 32L : 0)
            | (PetrifiedBloodCapped(conn, stats) ? 64L : 0)
            | (conn.Walk.Moving(conn.Clock.Elapsed.TotalSeconds) ? 128L : 0);
        if (Interlocked.Exchange(ref frame.Conditions, conditions) == conditions) return;
        foreach (var step in ConditionReaders) graph.MarkRule((int)step);
    }

    static bool PetrifiedBloodCapped(ClientState conn, IReadOnlyDictionary<uint, int> stats)
    {
        if (!conn.PetrifiedBlood.Active || conn.PlayerLife == 0) return false;
        var (lifeRegenPerMinute, _) = PanelStats.LifeRegeneration(
            name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, conn.Pools.Life);
        uint margin = PetrifiedBlood.RecoveryMargin(lifeRegenPerMinute / 60);
        return (long)conn.PlayerLife + margin >= (uint)((long)conn.PlayerMaxLife * LowLifeThresholdPct(stats) / 100);
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
        frame.Clock = clock;
        var graph = frame.Graph ??= PlayerStatGraph(conn, frame);
        if (!ReferenceEquals(frame.Area, conn.Frames.Area))
        {
            if (frame.Area is not null) PlayerStatBuffsChanged(conn);
            frame.Area = conn.Frames.Area;
        }
        lock (frame.Timed)
            foreach (var (step, (at, now)) in frame.Timed.ToList())
                if ((now?.Invoke() ?? clock) >= at) { frame.Timed.Remove(step); graph.MarkRule((int)step); }
        if (clock >= frame.MovingCheckAt) PlayerStatInputChanged(conn, StatInput.Walk);
        var items = ItemsOfUnread(conn);
        if (!ReferenceEquals(items, frame.Items) || items.Changes != frame.ItemChanges)
        {
            if (frame.Items is not null) graph.MarkInput((int)StatInput.Items);
            (frame.Items, frame.ItemChanges) = (items, items.Changes);
        }
        runningFrame = frame;
        bool built;
        try { built = graph.Step(); }
        finally { runningFrame = null; }
        if (frame.Conditions < 0 || built) CheckConditions(conn, frame, graph);
        if (VerifyFrameStats) VerifyFrame(conn, frame);
        frame.ByFrame = true;
        if (built) frame.Builds++;
        if (++frame.Steps == 300)
        {
            // Game_TRACE=budget: how often the phase changed them and published them (300 frames, about 10 s).
            if (GameServer.Core.Diagnostics.ServerTrace.Budget)
                Console.WriteLine($"[stats] {conn.Wire.Remote} {frame.Builds} change(s), {graph.RuleRuns} step run(s) so far, {frame.Published} panel(s) in {frame.Steps} frames");
            frame.Steps = frame.Builds = frame.Published = 0;
        }
        PlayerStatChange why;
        lock (frame)
        {
            if (frame.Publish is not { } asked || !frame.Publishing.IsCompleted) return;
            (why, frame.Publish) = (asked, null);
            frame.Published++;
        }
        var (remote, stream, ciphers) = conn.Wire;
        frame.Publishing = AsPlayer(conn, updates, () => PublishCharacterStats(remote, stream, ciphers, conn, why));
    }

    static StatGraph PlayerStatGraph(ClientState conn, FrameStats frame)
    {
        var graph = new StatGraph();
        foreach (var step in Enum.GetValues<PlayerStatStep>())
            graph.AddRule(view =>
            {
                // A run reports its timed effects again (StatsChangeIn), so the last run's go.
                lock (frame.Timed) frame.Timed.Remove(step);
                var outer = runningStep;
                runningStep = step;
                try { RunPlayerStep(step, conn, view, frame.Outputs); }
                finally { runningStep = outer; }
            });
        // The first step builds them all.
        for (int i = 0; i < PlayerSteps.Length; i++) graph.MarkRule(i);
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
            .Select(k => $"{Stats.Name(k)} {kept.GetValueOrDefault(k)}->{fresh.GetValueOrDefault(k)}").Order().ToArray();
        if (wrong.Length == 0) return;
        string names = string.Join(" ", wrong.Take(12));
        int seen = FrameStatsMisses.AddOrUpdate(string.Join(" ", wrong.Take(12).Select(w => w[..w.IndexOf(' ')])), 1, (_, n) => n + 1);
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
