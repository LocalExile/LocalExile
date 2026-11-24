using System.Runtime.CompilerServices;

public static partial class Game
{
    // Grants: one actor's rule gives OTHER actors a bundle of stats (Bundles.cs). The first step of "anything on
    // anything": a worn line can say "enemies in your area have Kaom's Roots", and each of those monsters then carries
    // the boots' stats as a layer of its own stats, with this player as the layer's source.
    //
    //   who gets it ("to")   how long
    //   enemies_area         while the rule stands: every living enemy in the area, the ones that arrive later too
    //   enemies_near         while the rule stands and the enemy is within "radius" cells of the actor
    //   enemy                the enemy of the event (the one hit, or the one that hit the actor), for "seconds"
    //
    // A layer is a SpawnedMonster temporary stat source (its key starts with "fx:<player id>:"), so the monster's stat
    // graph adds it up with its mods and takes it out again without touching anything else. Standing grants are kept by
    // a step a few times a second, which is what reaches monsters that were not there when the rule started; timed ones
    // are put on at their event and taken off by the same step. Everything a player gave comes off when they leave
    // (ForgetGrants), since the instance keeps its monsters for the next visit.
    //
    // What a monster does with a stat is the monster code's business: a stat no monster code reads does nothing on a
    // monster yet. "+# to maximum Life" is taught here (monsters had no flat life of their own).

    /// <summary>How often the standing grants are checked against the area's monsters, in milliseconds.</summary>
    const int GrantStepMs = 200;

    sealed record StandingGrant(string Key, string Name, IReadOnlyDictionary<uint, int> Stats, double Radius, Func<bool> On)
    {
        public bool Told;
    }

    sealed class GrantSet
    {
        public StandingGrant[] Standing = [];
        public readonly Dictionary<(SpawnedMonster Monster, string Key), (uint Id, double Until)> Timed = [];
        public int Serial;
        public bool Stepping;
    }

    static readonly ConditionalWeakTable<Player, GrantSet> GrantSets = new();
    static readonly ConditionalWeakTable<SpawnedMonster, StrongBox<bool>> GrantLifeFollows = new();
    // A bundle at one roll and effect, built once: an on-hit grant asks for the same one at every hit.
    static readonly ConditionalWeakTable<StatBundle, System.Collections.Concurrent.ConcurrentDictionary<(double, double, double, int), IReadOnlyDictionary<uint, int>>> BundleRolls = new();

    static IReadOnlyDictionary<uint, int> BundleAt(StatBundle bundle, double roll, double effect, double own) =>
        BundleRolls.GetOrCreateValue(bundle).GetOrAdd((roll, effect, own, ModScaling.Version), key => bundle.At(key.Item1, key.Item2, key.Item3));

    /// <summary>A "worn" rule's grant starts: the bundle goes to the enemies its "to" names for as long as the rule stands.</summary>
    static void StandGrant(EffectRules.Standing standing)
    {
        var player = standing.Conn;
        var bundle = (StatBundle)standing.Data!;
        double Number(string name) => standing.Values[Array.IndexOf(standing.Kind.Values, name)];
        string to = standing.Word("to") ?? "";
        if (to == "enemy") return;   // the enemy of an event: a "worn" rule has none (the table loader refuses it)
        var stats = BundleAt(bundle, Number("roll"), Number("effect"), Number("own"));
        if (stats.Count == 0) return;
        double radius = to == "enemies_near" ? Math.Max(1, Number("radius")) : 0;
        var set = GrantSets.GetOrCreateValue(player);
        lock (set)
            set.Standing = [.. set.Standing, new($"fx:{player.Id}:{++set.Serial}:{bundle.Name}", bundle.Name, stats, radius, standing.On)];
        Console.WriteLine($"[gateway] {player.Connection.Remote} gives \"{bundle.Name}\" ({stats.Count} stat(s)) to "
            + (radius > 0 ? $"enemies within {radius:0} cells" : "the enemies in the area") + " while its line is there");
        StartGrantSteps(player, set);
    }

    /// <summary>An event's grant: the bundle goes to the event's enemy for "seconds"; a later one restarts the time.</summary>
    static void GrantOnEvent(EffectRules.Fire fire)
    {
        if (fire.Word("to") != "enemy" || fire.Event.Monster is not { Dead: false, Life: > 0 } m) return;
        double seconds = fire.Number("seconds");
        if (seconds <= 0) return;
        var player = fire.Conn;
        var hit = fire.Event as HitLanded;
        var taken = fire.Event as ChargeEvent;
        uint id = hit?.MonsterId ?? taken?.MonsterId ?? 0;
        if (id == 0)
            foreach (var (other, monster) in player.Area.Monsters)
                if (ReferenceEquals(monster, m)) { id = other; break; }
        if (id == 0) return;
        var bundle = (StatBundle)fire.Data!;
        var stats = BundleAt(bundle, fire.Number("roll"), fire.Number("effect"), fire.Number("own"));
        if (stats.Count == 0) return;
        string key = $"fx:{player.Id}:hit:{bundle.Name}";
        double until = (hit?.At ?? taken?.HitAt ?? ProjectileWorldTime(player)) + seconds;
        var set = GrantSets.GetOrCreateValue(player);
        lock (set) set.Timed[(m, key)] = (id, until);
        if (!m.TemporaryStats.TryGetValue(key, out var has) || !ReferenceEquals(has, stats)) ChangeGrantLocked(player, id, m, key, stats);
        StartGrantSteps(player, set);
    }

    static void StartGrantSteps(Player player, GrantSet set)
    {
        lock (set)
        {
            if (set.Stepping) return;
            set.Stepping = true;
        }
        EveryAsPlayer(player, GrantStepMs, () =>
        {
            lock (set)
            {
                if (set.Standing.Length > 0 || set.Timed.Count > 0) return true;
                set.Stepping = false;
                return false;
            }
        }, StepGrants);
    }

    // One step: each standing grant is on exactly the living enemies it names, and a timed one that ran out comes off.
    static Task StepGrants(Player player)
    {
        if (!CurrentGameWork || !GrantSets.TryGetValue(player, out var set)) return Task.CompletedTask;
        using (ProjectileMutation(player))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return Task.CompletedTask;
            StepGrantsLocked(player, set, ProjectileWorldTime(player));
        }
        return Task.CompletedTask;
    }

    /// <summary>One step now, for a caller that is already inside the area's work (and for tests).</summary>
    public static void StepGrantsLocked(Player player, double now)
    {
        if (GrantSets.TryGetValue(player, out var set)) StepGrantsLocked(player, set, now);
    }

    static void StepGrantsLocked(Player player, GrantSet set, double now)
    {
        StandingGrant[] all;
        lock (set) all = set.Standing;
        if (all.Length > 0)
        {
            var on = new bool[all.Length];
            bool ended = false, near = false;
            for (int i = 0; i < all.Length; i++)
            {
                on[i] = all[i].On();
                ended |= !on[i];
                near |= on[i] && all[i].Radius > 0;
            }
            var at = near ? player.Walk.Position(now) : default;
            var gained = new int[all.Length];
            foreach (var (id, m) in player.Area.Monsters)
            {
                if (m.Dead) continue;   // a corpse keeps what it had
                bool enemy = m.Hostile && m.Life > 0;
                double away = -1;   // worked out once, for the first grant that has a radius
                for (int i = 0; i < all.Length; i++)
                {
                    var grant = all[i];
                    bool want = on[i] && enemy
                        && (grant.Radius <= 0 || (away < 0 ? away = Dist(at, m.Walk.Position(now)) : away) <= grant.Radius);
                    if (want == m.TemporaryStats.ContainsKey(grant.Key)) continue;
                    ChangeGrantLocked(player, id, m, grant.Key, want ? grant.Stats : null);
                    if (want) gained[i]++;
                }
            }
            for (int i = 0; i < all.Length; i++)
                if (on[i] && !all[i].Told && gained[i] > 0)
                {
                    all[i].Told = true;
                    Console.WriteLine($"[gateway] {player.Connection.Remote} \"{all[i].Name}\" is on {gained[i]} monster(s)");
                }
            // A grant whose rule is gone has been taken off above: it leaves the list.
            if (ended)
                lock (set) set.Standing = [.. set.Standing.Where(g => Array.IndexOf(all, g) is var i && (i < 0 || on[i]))];
        }
        lock (set)
        {
            if (set.Timed.Count == 0) return;
            foreach (var (key, (id, until)) in set.Timed.ToArray())
            {
                if (now < until && !key.Monster.Dead && EffectRules.Enabled) continue;
                set.Timed.Remove(key);
                if (!key.Monster.Dead) ChangeGrantLocked(player, id, key.Monster, key.Key, null);
            }
        }
    }

    /// <summary>Puts a granted layer on a monster (or takes it off: <paramref name="stats"/> null). Its maximum life
    /// follows from the frame its stats are built in, as a shrine's aura does it (MonsterAuraEffect), and flat life
    /// ("+# to maximum Life") counts here: it is added before the monster's own percent modifiers.</summary>
    static void ChangeGrantLocked(Player player, uint id, SpawnedMonster m, string key, IReadOnlyDictionary<uint, int>? stats, bool tell = true)
    {
        // One follow-up per build, from the stats as they were before the first change of the frame: two layers
        // changed in one frame are one step from before to after.
        var follows = GrantLifeFollows.GetOrCreateValue(m);
        var was = follows.Value || m.Dead ? null : m.Effects;
        if (stats is null) { if (!m.RemoveTemporaryStats(key)) return; }
        else m.SetTemporaryStats(key, stats);
        if (was is null) return;
        follows.Value = true;
        m.AfterStatsRebuild((_, effects) =>
        {
            follows.Value = false;
            int before = was.MaxLife(100000), after = effects.MaxLife(100000);
            int flat = effects[Stat.BaseMaximumLife] - was[Stat.BaseMaximumLife];
            if (before == after && flat == 0) return;
            uint oldLife = m.MaxLife;
            double own = oldLife * 100000.0 / Math.Max(1, before);   // its life before its percent modifiers
            uint maximum = (uint)Math.Clamp(Math.Round((own + flat) * after / 100000.0), 1, uint.MaxValue);
            if (maximum == oldLife) return;
            m.MaxLife = maximum;
            if (m.Life > 0) m.Life = (uint)Math.Clamp(Math.Ceiling(m.Life * (double)maximum / Math.Max(1, oldLife)), 1, maximum);
            if (tell && !player.Closed && !player.LeavingArea) SetStats(player, id, [(Stat.MaximumLife, (int)Math.Min(int.MaxValue, maximum))]);
        });
    }

    /// <summary>A leaving player's grants come off the instance's monsters, which stay for the next visit.</summary>
    public static void ForgetGrants(Player player)
    {
        if (!GrantSets.TryGetValue(player, out var set)) return;
        lock (set) { set.Standing = []; set.Timed.Clear(); }
        string prefix = $"fx:{player.Id}:";
        void Forget(IEnumerable<KeyValuePair<uint, SpawnedMonster>> monsters)
        {
            foreach (var (id, m) in monsters)
            {
                if (m.TemporaryStats.Count == 0) continue;
                foreach (string key in m.TemporaryStats.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                    ChangeGrantLocked(player, id, m, key, null, tell: false);
            }
        }
        Forget(player.Area.Monsters);
        Forget(player.Area.DormantMonsters);
    }
}
