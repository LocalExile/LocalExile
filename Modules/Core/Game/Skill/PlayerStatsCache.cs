using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

// The player's stats. In an area's frames they are the player's stat graph, stepped once a frame in the Stats phase (below):
// a read between two steps sees the last one. Off the frames (tests, tools) a read builds them in full.
public static partial class Game
{
    // What the stats read of mana is its conditions (ItemConditionFlags: full, half or less) and empty: a change of the
    // amount alone marks nothing (CheckConditions).
    static long ManaState(Player player)
    {
        long mana = player.PlayerMana, pool = player.Pools.Mana;
        return (mana >= pool ? 1 : 0) | (mana * 2 <= pool ? 2 : 0) | (mana == 0 ? 4 : 0);
    }

    /// <summary>One of the player's stats: in an area's frames a read of the graph's last step, no copy; off them a build.
    /// For a per-hit or per-kill read of a few stats.</summary>
    public static int PlayerStat(Player player, uint key) =>
        FrameStatsOf.TryGetValue(player, out var frame) && frame.ByFrame && frame.Graph is { } graph
            ? graph.Values.GetValueOrDefault(key) : CachedPlayerStats(player, out _).GetValueOrDefault(key);

    /// <summary>The raw stats of the passives and items (CharacterStatSources): in an area's frames those the graph's last
    /// Sources step summed, not to be changed; off them worked out now.</summary>
    /// A stat rule works them out itself, so the graph records what it read.
    public static IReadOnlyDictionary<uint, int> StatSourceValues(Player player) =>
        !StatGraph.InRule && FrameStatsOf.TryGetValue(player, out var frame) && frame.ByFrame && frame.Outputs.Sources is { } sources
            ? sources : CharacterStatSources(player).Stats;

    /// <summary>The player's stats, not to be changed: in an area's frames the graph's last step's values, off them a
    /// fresh build.</summary>
    public static IReadOnlyDictionary<uint, int> PlayerStatValues(Player player) =>
        FrameStatsOf.TryGetValue(player, out var frame) && frame.ByFrame && frame.Graph is { } graph
            ? graph.Values : CharacterPlayerStatsSnapshot(player, out _, out _);

    public static Dictionary<uint, int> CachedPlayerStats(Player player, out double baselineLifePerSecond) =>
        CachedPlayerStats(player, out baselineLifePerSecond, out _);

    /// <summary>The stats as of the graph's last step in an area's frames, else a fresh build; with
    /// <paramref name="capturePanelSources"/>, the panel's sources too (what the tail's first step saw).</summary>
    public static Dictionary<uint, int> CachedPlayerStats(Player player, out double baselineLifePerSecond,
        out PanelSourceSnapshot? panelSources, bool capturePanelSources = false)
    {
        if (FrameStatsOf.TryGetValue(player, out var frame) && frame.ByFrame && frame.Graph is { } graph)
        {
            baselineLifePerSecond = frame.Outputs.BaselineLifePerSecond;
            panelSources = capturePanelSources ? PanelSources(player, graph.ValuesBefore((int)PlayerStatStep.TailPanels)) : null;
            return new(graph.Values);
        }
        return CharacterPlayerStatsSnapshot(player, out baselineLifePerSecond, out panelSources, capturePanelSources);
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
        // The graph's values changed: the stats (and the skills that follow them) go to the client.
        public bool PublishValues;
        // The values or a condition changed: PlayerStatsUpdated runs after the step.
        public volatile bool Updated;
        public Task Publishing = Task.CompletedTask;
        public int Steps, Builds, Published;
        // Game_TRACE=budget: the runs of each step in the window.
        public readonly int[] StepRuns = new int[Enum.GetValues<PlayerStatStep>().Length];
        // The steps whose timed effect changes by itself, and when (connection clock): StatsChangeIn.
        // Now: the clock the time is on (StatsChangeAt); null for the connection's.
        // A step may read effects on several clocks: one time per clock (the connection's, then each other by identity).
        public readonly Dictionary<PlayerStatStep, List<(double At, Func<double>? Now)>> Timed = [];
        // The conditions of life, mana and moving the steps read, as last marked; when moving next changes by itself.
        public long Conditions = -1;
        public double MovingCheckAt = double.PositiveInfinity;
        // Until when the player has hit an enemy recently, as last marked: a hit inside it changes nothing the steps read.
        public double HitRecentlyUntil = double.NegativeInfinity;
        public double Clock;
        // The area the steps last ran in: a new one has none of the last one's stat buffs.
        public GameArea? Area;
        // The items the steps last saw, and their change count (StatInput.Items).
        public PlayerInventories? Items;
        public long ItemChanges;
    }

    // The pools from what they are built of now (a new Pools marks the steps that read them). Smaller pools hold no more
    // than they can; a larger one is not refilled.
    static void RebuildPools(Player player)
    {
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.PlayerLife = Math.Min(player.PlayerLife, player.PlayerUnreservedLife);
        player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        player.ApplyMovementSpeedAt(AreaTime(player));
    }

    /// <summary>The area's stat buffs on a player's entity changed (AreaStatBuffs): its pools and sources are built again.</summary>
    internal static void PlayerStatBuffsChanged(Player player)
    {
        if (!FrameStatsOf.TryGetValue(player, out var frame) || frame.Graph is not { } graph) return;
        RebuildPools(player);
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
        AddTimed(frame, at, null);
    }

    // StatsChangeIn inside a condition.
    static bool StatsChangeInAnd(double seconds) { StatsChangeIn(seconds); return true; }

    /// <summary>As <see cref="StatsChangeIn"/>, for an effect timed on another clock than the connection's (the
    /// projectile runtime's): the step runs again once <paramref name="now"/> reaches <paramref name="at"/>.</summary>
    public static void StatsChangeAt(double at, Func<double> now)
    {
        if (runningFrame is not { } frame) return;
        AddTimed(frame, at, now);
    }

    // The earliest time for the running step on that clock.
    static void AddTimed(FrameStats frame, double at, Func<double>? now)
    {
        lock (frame.Timed)
        {
            if (!frame.Timed.TryGetValue(runningStep, out var times)) frame.Timed[runningStep] = times = [];
            int i = times.FindIndex(t => t.Now == now);
            if (i < 0) times.Add((at, now));
            else if (at < times[i].At) times[i] = (at, now);
        }
    }
    static readonly ConditionalWeakTable<Player, FrameStats> FrameStatsOf = new();

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
            PlayerStatStep.Sources, PlayerStatStep.Auras, PlayerStatStep.AuraPenetration,
            PlayerStatStep.CatAspect, PlayerStatStep.AvianCrab, PlayerStatStep.SelfVulnerability, PlayerStatStep.VaalAura,
            PlayerStatStep.SpiritLeadership, PlayerStatStep.TotemAura, PlayerStatStep.AuraRestriction,
        ];
        readers[(int)PlayerStatChange.Flasks] =
            [PlayerStatStep.Sources, PlayerStatStep.UtilityFlasks, PlayerStatStep.SelfVulnerability,
             PlayerStatStep.Onslaught, PlayerStatStep.UsingFlask, PlayerStatStep.AuraRestriction];
        readers[(int)PlayerStatChange.Minions] = [PlayerStatStep.Sources, PlayerStatStep.Golems, PlayerStatStep.Offerings];
        readers[(int)PlayerStatChange.Leech] =
            [PlayerStatStep.Sources, PlayerStatStep.AttackSpeed, PlayerStatStep.Recovery, PlayerStatStep.LeechingDefences];
        readers[(int)PlayerStatChange.ElementalAilments] = [PlayerStatStep.Sources, PlayerStatStep.TailCritical];
        readers[(int)PlayerStatChange.IncomingAilments] = [PlayerStatStep.Sources];
        readers[(int)PlayerStatChange.God] = [PlayerStatStep.Sources, PlayerStatStep.God];
        readers[(int)PlayerStatChange.Panel] = [];
        readers[(int)PlayerStatChange.Resonance] = [PlayerStatStep.Resonance];
        return readers;
    }

    // What a build change changes that the steps read as tracked inputs: whatever step read them runs again.
    static readonly StatInput[] BuildInputs = [StatInput.Items, StatInput.Passives, StatInput.Pools];

    // In an area's frames: marks the steps that read the change and asks the Stats phase to publish the panel
    // (SendCharacterStats); false off the frames, which publish at once.
    static bool PublishByFrame(Player player, PlayerStatChange change)
    {
        if (!FrameStatsOf.TryGetValue(player, out var frame) || !frame.ByFrame || frame.Graph is not { } graph) return false;
        foreach (var step in PlayerStatChangeReaders[(int)change]) graph.MarkRule((int)step);
        if (change == PlayerStatChange.Build)
            foreach (var input in BuildInputs) graph.MarkInput((int)input);
        // Only a build or an aura can change the skill set, the reservations or the bar: they take the full publication
        // (PublishCharacterStats). Any other change is new values (PublishStatValues, which takes the full one itself when
        // the skill set changed after all).
        lock (frame)
            if (change is PlayerStatChange.Build or PlayerStatChange.Auras) frame.Publish ??= change;
            else frame.PublishValues = true;
        return true;
    }

    // What reads the pools, and what reads the conditions of life, mana, Energy Shield and moving (the sources' "while"
    // conditions, the conditional pools, recovery and leech, the tail's low life and recharge rows), and what death ends.
    static readonly PlayerStatStep[] ConditionReaders =
    [
        PlayerStatStep.Sources, PlayerStatStep.CryMovement, PlayerStatStep.SpiritLeadership,
        PlayerStatStep.ClassAttributes, PlayerStatStep.Onslaught, PlayerStatStep.VaalPrevention, PlayerStatStep.TotemAura,
        PlayerStatStep.Rage, PlayerStatStep.Blind, PlayerStatStep.Recovery, PlayerStatStep.Rampage,
        PlayerStatStep.TailPanels, PlayerStatStep.TailOffence, PlayerStatStep.TailCritical, PlayerStatStep.Resonance,
    ];

    /// <summary>The framework's hand-over (Player.StatsChanged): marks the steps that read the input.</summary>
    internal static void PlayerStatInputChanged(Player player, StatInput input)
    {
        if (!FrameStatsOf.TryGetValue(player, out var frame) || frame.Graph is not { } graph) return;
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
                if (graph.Values.GetValueOrDefault(RecentEnemyHits.PerHit) != 0
                    || graph.Values.GetValueOrDefault(RecentEnemyHits.GenericChance) != 0)
                    graph.MarkRule((int)PlayerStatStep.RecentHits);
                break;
            case StatInput.FlaskEffects:
                foreach (var step in PlayerStatChangeReaders[(int)PlayerStatChange.Flasks]) graph.MarkRule((int)step);
                break;
            case StatInput.RecentEvents:
                graph.MarkRule((int)PlayerStatStep.Sources);
                break;
            case StatInput.EnemyHit:
                // "Hit an enemy recently" (the Spell Suppression chance): only a start marks; the step times its end.
                if (player.LastEnemyHitAt > frame.HitRecentlyUntil) graph.MarkRule((int)PlayerStatStep.TailCritical);
                frame.HitRecentlyUntil = player.LastEnemyHitAt + RecentlySeconds;
                break;
            case StatInput.Walk:
                graph.MarkRule((int)PlayerStatStep.TotemAura);
                double clock = AreaTime(player);
                frame.MovingCheckAt = player.Walk.MovingChangesIn(clock) is double next ? clock + next : double.PositiveInfinity;
                CheckConditions(player, frame, graph);
                break;
            default:
                CheckConditions(player, frame, graph);
                break;
        }
    }

    // Life, mana and moving change often (regeneration, every step of a walk); what the steps read of them is a few
    // conditions, and only a change of one marks them.
    static void CheckConditions(Player player, FrameStats frame, StatGraph graph)
    {
        var stats = graph.Values;
        long conditions = ManaState(player)
            | (player.PlayerLife == 0 ? 8L : 0) | (CriticalOnLowLife(player, stats) ? 16L : 0)
            | (CountsAsFullLife(player.PlayerLife, player.PlayerMaxLife, stats) ? 32L : 0)
            | (PetrifiedBloodCapped(player, stats) ? 64L : 0)
            | (player.Walk.Moving(AreaTime(player)) ? 128L : 0);
        if (Interlocked.Exchange(ref frame.Conditions, conditions) == conditions) return;
        frame.Updated = true;
        foreach (var step in ConditionReaders) graph.MarkRule((int)step);
    }

    static bool PetrifiedBloodCapped(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        if (!player.PetrifiedBlood.Active || player.PlayerLife == 0) return false;
        var (lifeRegenPerMinute, _) = PanelStats.LifeRegeneration(
            name => name is var key ? stats.GetValueOrDefault(key) : 0, player.Pools.Life);
        double factor = LifeRegenerationKeystoneFactor(name => stats.GetValueOrDefault(name));
        uint margin = PetrifiedBlood.RecoveryMargin(lifeRegenPerMinute * factor / 60);
        return (long)player.PlayerLife + margin >= (uint)((long)player.PlayerMaxLife * LowLifeThresholdPct(stats) / 100);
    }

    static readonly bool VerifyFrameStats = Environment.GetEnvironmentVariable("Game_STATS_FRAME_VERIFY") == "1";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FrameStatsMisses = new();

    /// <summary>The player's part of the Stats phase.</summary>
    internal static void StepPlayerStats(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || !player.Connection.PlayerSent
            || player.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(player, updates)) return;
        StepOwnAuras(player);
        var frame = FrameStatsOf.GetValue(player, _ => new());
        double clock = AreaTime(player);
        frame.Clock = clock;
        var graph = frame.Graph ??= PlayerStatGraph(player, frame);
        CheckMomentumPresentation(player, clock);
        RefreshDoubleStrikeMomentum(player, clock);
        if (!ReferenceEquals(frame.Area, player.Frames.Area))
        {
            if (frame.Area is not null) PlayerStatBuffsChanged(player);
            frame.Area = player.Frames.Area;
        }
        lock (frame.Timed)
            foreach (var (step, times) in frame.Timed.ToList())
                if (times.Any(t => (t.Now?.Invoke() ?? clock) >= t.At)) { frame.Timed.Remove(step); graph.MarkRule((int)step); }
        if (clock >= frame.MovingCheckAt) PlayerStatInputChanged(player, StatInput.Walk);
        var items = ItemsOfUnread(player);
        if (!ReferenceEquals(items, frame.Items) || items.Changes != frame.ItemChanges)
        {
            if (frame.Items is not null) graph.MarkInput((int)StatInput.Items);
            (frame.Items, frame.ItemChanges) = (items, items.Changes);
        }
        runningFrame = frame;
        bool built;
        try { built = graph.Step(); }
        finally { runningFrame = null; }
        if (frame.Conditions < 0 || built) CheckConditions(player, frame, graph);
        if (built || frame.Updated)
        {
            frame.Updated = false;
            PlayerStatsUpdated(player, graph.Values);
        }
        // The skills follow the stats in the same phase (ActiveSkills): a use in the next frame reads them.
        if (built)
        {
            // Maxima and reservations follow the stats: the Life component compares the pools at the frame's end.
            player.Frames.Area?.Life.Changed(player);
            PlayerSkills(player);
            ReadyActiveSkills(player);
        }
        if (VerifyFrameStats) VerifyFrame(player, frame);
        frame.ByFrame = true;
        if (built) frame.Builds++;
        if (++frame.Steps == 300)
        {
            // Game_TRACE=budget: how often the phase changed them and published them (300 frames, about 10 s).
            if (GameServer.Core.Diagnostics.ServerTrace.Budget)
            {
                var busiest = frame.StepRuns.Select((runs, step) => (runs, step: (PlayerStatStep)step)).Where(r => r.runs > 0)
                    .OrderByDescending(r => r.runs).Take(6).Select(r => $"{r.step} {r.runs}");
                Console.WriteLine($"[stats] {player.Connection.Remote} {frame.Builds} change(s), {graph.RuleRuns} step run(s) so far, {frame.Published} panel(s) in {frame.Steps} frames; {string.Join(", ", busiest)}");
            }
            Array.Clear(frame.StepRuns);
            frame.Steps = frame.Builds = frame.Published = 0;
        }
        RunStatPresentations(player);
        PlayerStatChange? why;
        lock (frame)
        {
            if (built) frame.PublishValues = true;
            if (!frame.Publishing.IsCompleted || frame.Publish is null && !frame.PublishValues) return;
            (why, frame.Publish, frame.PublishValues) = (frame.Publish, null, false);
            frame.Published++;
        }
        frame.Publishing = AsPlayer(player, updates, () => why is { } asked
            ? PublishCharacterStats(player, asked)
            : PublishStatValues(player));
    }

    static StatGraph PlayerStatGraph(Player player, FrameStats frame)
    {
        var graph = new StatGraph();
        foreach (var step in Enum.GetValues<PlayerStatStep>())
            graph.AddRule(view =>
            {
                // A run reports its timed effects again (StatsChangeIn), so the last run's go.
                lock (frame.Timed) frame.Timed.Remove(step);
                frame.StepRuns[(int)step]++;
                var outer = runningStep;
                runningStep = step;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { RunPlayerStep(step, player, view, frame.Outputs); }
                finally
                {
                    runningStep = outer;
                    double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    // A step this slow is a frame the client waits for.
                    if (ms >= 10) Console.WriteLine($"[stats] {player.Connection.Remote} step {step} took {ms:0} ms");
                }
            });
        // The first step builds them all.
        for (int i = 0; i < PlayerSteps.Length; i++) graph.MarkRule(i);
        // The connection's timed states report their changes; the steps that read them report when they change by
        // themselves (StatsChangeIn, StatsChangeAt).
        void On(PlayerStatStep[] steps, Action<Action> wire) => wire(() => { foreach (var s in steps) MarkPlayerStats(player, s); });
        On([PlayerStatStep.Sources], a => player.RecentEvents.Changed = a);
        player.RecentEvents.AmountChanged = key =>
        {
            if (key == StatConditions.UsedSkill && MapSkillActionSpeedPerUse(player) != 0)
                MarkPlayerStats(player, PlayerStatStep.TailCritical);
            if (player.StatSourcesCache?.Key.Amounts?.WasRead(key) == true)
                MarkPlayerStats(player, PlayerStatStep.Sources);
        };
        On([PlayerStatStep.Rage], a => player.Rage.Changed = a);
        // A soul eaten at the cap changes no stat, but its beam is shown.
        On([PlayerStatStep.SoulEater, PlayerStatStep.AttackSpeed], a => player.SoulEater.Changed = () => { a(); PresentStats(player); });
        On([PlayerStatStep.SpiritLeadership], a => player.SpiritChieftains.Changed = a);
        On([PlayerStatStep.Ward], a => player.Ward.Changed = a);
        On([PlayerStatStep.Sources, PlayerStatStep.TimedRecovery], a => player.Antiquarian.Changed = a);
        On([PlayerStatStep.TimedRecovery], a => player.EnduringCryRecovery.Changed = a);
        On([PlayerStatStep.TotemAura], a => player.Totems.Changed = a);
        On([PlayerStatStep.Rampage], a => player.Rampage.Changed = a);
        On(PlayerStatChangeReaders[(int)PlayerStatChange.Leech], a => LeechOf(player).Changed = a);
        LeechOf(player).DisplayChanged = () => PresentStats(player);
        player.PetrifiedBlood.Changed = () => PresentStats(player);
        player.PhysicalAegis.Changed = () => PresentStats(player);
        player.BlockersChanged = () => PresentNearby(player);
        player.AuraStatsDirtied = () => PresentNearby(player);
        ItemsOf(player).Changed = () => PresentStats(player);
        On(PlayerStatChangeReaders[(int)PlayerStatChange.IncomingAilments],
            a => player.IncomingAilments.Changed = () => { a(); PresentStats(player); });
        On(PlayerStatChangeReaders[(int)PlayerStatChange.ElementalAilments],
            a => player.ElementalAilments.Changed = () => { a(); PresentStats(player); });
        return graph;
    }

    /// <summary>A change that one step reads: that step runs again in the next Stats phase (and what it changes, after it).</summary>
    /// <summary>For a step whose result a later step reads as state (not as stats): that step runs in this pass. Only
    /// a run in the graph marks; the steps run in order on a plain map (a fresh build, the verifier) reach it anyway.</summary>
    internal static void MarkLaterStep(Player player, PlayerStatStep step)
    {
        if (runningFrame is not null) MarkPlayerStats(player, step);
    }

    /// <summary>Whether the area's Stats phase publishes the player's stats when they change: a send of them after a change
    /// would carry the last phase's values, so it sends nothing (<see cref="PlayerStatsOffFrames"/>).</summary>
    public static bool StatsPublishedByFrame(Player player) =>
        FrameStatsOf.TryGetValue(player, out var frame) && frame.ByFrame && frame.Graph is not null;

    /// <summary>The player's stats as a 0x1b3 after a change, off an area's frames (empty when the frame publishes them).</summary>
    public static byte[] PlayerStatsOffFrames(Player player) =>
        StatsPublishedByFrame(player) ? [] : PublishPlayerStats(player);

    /// <summary>Marks <paramref name="step"/> and is true: for a condition that started an effect the step reads.</summary>
    internal static bool Marked(Player player, PlayerStatStep step) { MarkPlayerStats(player, step); return true; }

    public static void MarkPlayerStats(Player player, PlayerStatStep step)
    {
        if (FrameStatsOf.TryGetValue(player, out var frame) && frame.Graph is { } graph) graph.MarkRule((int)step);
    }

    internal static int PlayerStatStepRuns(Player player, PlayerStatStep step) =>
        FrameStatsOf.TryGetValue(player, out var frame) ? frame.StepRuns[(int)step] : 0;

    // A mark or a timed end waits for the next Stats phase (the verifiers compare only once it ran).
    static bool StatsUnsettled(Player player)
    {
        if (!FrameStatsOf.TryGetValue(player, out var frame) || frame.Graph is not { } graph) return false;
        if (graph.MarksPending) return true;
        double clock = AreaTime(player);
        lock (frame.Timed)
            return frame.Timed.Values.Any(times => times.Any(t => (t.Now?.Invoke() ?? clock) >= t.At));
    }

    // Game_STATS_FRAME_VERIFY=1: the graph's values against every step run in order now; what differs is a change no
    // mark reached.
    static void VerifyFrame(Player player, FrameStats frame)
    {
        if (frame.Graph is not { } graph) return;
        var kept = graph.Values;
        var fresh = CharacterPlayerStatsSnapshot(player, out _, out _);
        // A change made on another thread after the step, or a timed end reached since, is the next step's: its mark waits.
        if (graph.MarksPending) return;
        double clock = AreaTime(player);
        lock (frame.Timed)
            if (frame.Timed.Values.Any(times => times.Any(t => (t.Now?.Invoke() ?? clock) >= t.At))) return;
        var wrong = fresh.Keys.Union(kept.Keys).Where(k => fresh.GetValueOrDefault(k) != kept.GetValueOrDefault(k))
            .Select(k => $"{Stats.Name(k)} {kept.GetValueOrDefault(k)}->{fresh.GetValueOrDefault(k)}").Order().ToArray();
        if (wrong.Length == 0) return;
        string names = string.Join(" ", wrong.Take(12));
        int seen = FrameStatsMisses.AddOrUpdate(string.Join(" ", wrong.Take(12).Select(w => w[..w.IndexOf(' ')])), 1, (_, n) => n + 1);
        if (seen is 1 or 10 or 100 or 1000)
            Console.WriteLine($"[stats-verify] {player.Connection.Remote} {wrong.Length} stat(s) changed unmarked (x{seen}): {names}");
    }

    // ---- the skill list ----

    // The last full skill list (0x2bc, no removals) this client was sent: the Rage, charge, aura, Fortification, Onslaught,
    // Rampage and Antiquarian refreshes resend it whole, about 32 KB, at every change of theirs, also when not one skill
    // changed. An unchanged list is left out. The full refresh (SendCharacterStats) always sends and records it.
    sealed class SentSkillList { public byte[]? Bytes; public IReadOnlyList<GrantedSkill>? Skills; }
    static readonly Store<Player, SentSkillList> SentSkillLists = new();

    public static byte[] PlayerSkillsIfChanged(Player player, IReadOnlyList<GrantedSkill> skills) =>
        [.. PlayerSkillsBatchesIfChanged(player, skills).SelectMany(p => p)];

    public static byte[][] PlayerSkillsBatchesIfChanged(Player player, IReadOnlyList<GrantedSkill> skills)
    {
        var batches = InstanceEntities.PlayerSkillsChangeBatches(player.Id, skills, [], canDetonateMines: CanDetonateMines(player));
        byte[] flat = batches.Length == 1 ? batches[0] : [.. batches.SelectMany(p => p)];
        var sent = SentSkillLists.GetOrCreateValue(player);
        lock (sent)
        {
            if (sent.Bytes is { } last && last.AsSpan().SequenceEqual(flat)) return [];
            if (DeferMomentumChannelSkills(player) && player.ClientSkillIds.SetEquals(skills.Select(s => s.Id))) return [];
            (sent.Bytes, sent.Skills) = (flat, skills);
        }
        // Any 0x02bc Actor skill-list update can reset client-side skill bar bindings; schedule a coalesced 0x0053 restore.
        _ = RestoreSkillBarAfterActorUpdate(player);
        return batches;
    }

    // What the full refresh sent: the base the next partial refresh compares with.
    public static void RecordPlayerSkills(Player player, IReadOnlyList<GrantedSkill> skills)
    {
        var batches = InstanceEntities.PlayerSkillsChangeBatches(player.Id, skills, [], canDetonateMines: CanDetonateMines(player));
        byte[] flat = batches.Length == 1 ? batches[0] : [.. batches.SelectMany(p => p)];
        var sent = SentSkillLists.GetOrCreateValue(player);
        lock (sent) (sent.Bytes, sent.Skills) = (flat, skills);
    }

    /// <summary>A stat of the skill as the client last received it in the skill list (0 when the skill went without it),
    /// null when the skill was not sent. The client times some effects by the skill's stats it holds, not the
    /// character's current ones (Hammerfalls' waves).</summary>
    public static int? SentSkillStat(Player player, ushort skillId, uint stat)
    {
        if (!(stat is var key) || !SentSkillLists.TryGetValue(player, out var sent)) return null;
        lock (sent)
        {
            if (sent.Skills?.FirstOrDefault(s => s.Id == skillId) is not { } skill) return null;
            foreach (var (k, value) in skill.Stats ?? []) if (k == key) return value;
            return 0;
        }
    }
}
