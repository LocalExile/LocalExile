using GameServer.Core.Protocol.Instance;
using System.Runtime.CompilerServices;

public static partial class Game
{
    // Grants: one actor's rule gives OTHER actors a bundle of stats (Bundles.cs). The first step of "anything on
    // anything": a worn line can say "enemies in your area have Kaom's Roots", and each of those monsters then carries
    // the boots' stats as a layer of its own stats, with this player as the layer's source.
    //
    //   who gets it ("to")   how long
    //   enemies_area         while the rule stands: every living enemy in the area, the ones that arrive later too
    //   enemies_near         while the rule stands and the enemy is within "radius" cells of the actor (the bundle's
    //                        own reach when the row gives none: a template says how far its aura goes)
    //   enemy                the enemy of the event (the one hit, or the one that hit the actor), for "seconds"
    //   self                 the actor: while the rule stands, or for "seconds" from an event
    //   players_area         while the rule stands: every player in the area, the actor included
    //
    // A player carries a bundle as stats of its own (standing stats, or a timed buff). A bundle that says what it shows
    // as ("shows", or a template's own buff) puts that buff on whoever carries it.
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
        /// <summary>The buff its carriers are shown with; null for none.</summary>
        public ushort? Shows { get; init; }
        /// <summary>For the players of the area rather than its enemies: the stats as a player has them, and who has
        /// been given them so far.</summary>
        public (uint Key, int Value)[]? ForPlayers { get; init; }
        public readonly HashSet<Player> Given = [];
    }

    /// <summary>A timed buff without a buff of the client's to show.</summary>
    const ushort NoBuffRow = ushort.MaxValue;
    static readonly ConditionalWeakTable<IReadOnlyDictionary<uint, int>, BuffSpec> GrantSpecs = new();
    static readonly ConditionalWeakTable<IReadOnlyDictionary<uint, int>, IReadOnlyDictionary<uint, int>> GrantsForMonsters = new();

    /// <summary>A bundle's stats as a monster reads them: a stat the tables name another for (fx_reads_as, actor
    /// "monster": elemental damage taken is the three elements' on a monster) is given as that one, times its scale;
    /// every other stat as it is.</summary>
    static IReadOnlyDictionary<uint, int> ForMonsters(IReadOnlyDictionary<uint, int> stats) =>
        GrantsForMonsters.GetValue(stats, given =>
        {
            var reads = MonsterReadsAs();
            if (reads.Count == 0 || !given.Keys.Any(reads.ContainsKey)) return given;
            var result = new Dictionary<uint, int>();
            foreach (var (stat, value) in given)
                if (reads.TryGetValue(stat, out var others))
                    foreach (var (other, scale) in others)
                        result[other] = ActorScale.Saturate(result.GetValueOrDefault(other) + (long)Math.Round(value * scale, MidpointRounding.AwayFromZero));
                else result[stat] = ActorScale.Saturate((long)result.GetValueOrDefault(stat) + value);
            return result;
        });

    static (object Rows, Dictionary<uint, (uint Stat, double Scale)[]> Map)? _monsterReads;
    static Dictionary<uint, (uint Stat, double Scale)[]> MonsterReadsAs()
    {
        var rows = GameData.FxReadsAs;
        var known = _monsterReads;
        if (known is { } has && ReferenceEquals(has.Rows, rows)) return has.Map;
        var map = rows.Where(r => r.Actor == "monster" && Program.Stats.Key(r.Stat) is not null && Program.Stats.Key(r.Reads) is not null)
            .GroupBy(r => Program.Stats.Key(r.Stat)!.Value)
            .ToDictionary(g => g.Key, g => g.Select(r => (Program.Stats.Key(r.Reads)!.Value, r.Scale)).ToArray());
        _monsterReads = (rows, map);
        return map;
    }

    sealed class GrantSet
    {
        public StandingGrant[] Standing = [];
        public readonly Dictionary<(SpawnedMonster Monster, string Key), (uint Id, double Until, ushort? Shows)> Timed = [];
        public int Serial;
        public bool Stepping;
    }

    static readonly ConditionalWeakTable<Player, GrantSet> GrantSets = new();
    static readonly ConditionalWeakTable<SpawnedMonster, StrongBox<bool>> GrantLifeFollows = new();
    // A bundle at one roll and effect, built once: an on-hit grant asks for the same one at every hit.
    static readonly ConditionalWeakTable<StatBundle, System.Collections.Concurrent.ConcurrentDictionary<(double, double, double, int), IReadOnlyDictionary<uint, int>>> BundleRolls = new();

    static IReadOnlyDictionary<uint, int> BundleAt(StatBundle bundle, double roll, double effect, double own) =>
        BundleRolls.GetOrCreateValue(bundle).GetOrAdd((roll, effect, own, ModScaling.Version), key => bundle.At(key.Item1, key.Item2, key.Item3));

    /// <summary>A "worn" rule's grant starts: the bundle goes to whoever its "to" names for as long as the rule stands.</summary>
    static void StandGrant(EffectRules.Standing standing)
    {
        var player = standing.Conn;
        var bundle = (StatBundle)standing.Data!;
        double Number(string name) => standing.Values[Array.IndexOf(standing.Kind.Values, name)];
        string to = standing.Word("to") ?? "";
        if (to == "enemy") return;   // the enemy of an event: a "worn" rule has none (the table loader refuses it)
        if (bundle.OfLine)
        {
            // The giving line's own template: what it gives, how far and what it shows as come from the game's data.
            if (standing.Source?.Thing is not Item item || LineBundle(item, standing.Stat, ItemsFor(player).Mods) is not { } own)
            {
                Console.WriteLine($"[rules] a grant of \"the line's own\" found no line with a template for {Program.Stats.Name(standing.Stat)}");
                return;
            }
            bundle = own;
        }
        var stats = BundleAt(bundle, Number("roll"), Number("effect"), Number("own"));
        if (stats.Count == 0) return;
        if (to == "self")
        {
            GainStandingStats(player, [.. stats.Select(s => (s.Key, s.Value))], standing.On);
            Console.WriteLine($"[gateway] {player.Connection.Remote} has \"{bundle.Name}\" ({stats.Count} stat(s)) while its line is there");
            return;
        }
        double radius = to != "enemies_near" ? 0 : Number("radius") > 0 ? Number("radius") : bundle.Radius;
        if (to == "enemies_near" && radius <= 0)
        {
            Console.WriteLine($"[rules] a grant of \"{bundle.Name}\" to enemies_near has no radius: neither its row nor the bundle says one");
            return;
        }
        var set = GrantSets.GetOrCreateValue(player);
        bool players = to == "players_area";
        lock (set)
            set.Standing = [.. set.Standing, new($"fx:{player.Id}:{++set.Serial}:{bundle.Name}", bundle.Name,
                players ? stats : ForMonsters(stats), radius, standing.On)
                { Shows = bundle.Shows, ForPlayers = players ? [.. stats.Select(s => (s.Key, s.Value))] : null }];
        Console.WriteLine($"[gateway] {player.Connection.Remote} gives \"{bundle.Name}\" ({stats.Count} stat(s)) to "
            + (players ? "the players in the area" : radius > 0 ? $"enemies within {radius:0} cells" : "the enemies in the area") + " while its line is there");
        StartGrantSteps(player, set);
    }

    /// <summary>An event's grant: the bundle goes to the event's enemy, or to the actor, for "seconds"; a later one
    /// restarts the time.</summary>
    static void GrantOnEvent(EffectRules.Fire fire)
    {
        string? to = fire.Word("to");
        double seconds = fire.Number("seconds");
        if (seconds <= 0 || to is not ("enemy" or "self")) return;
        var player = fire.Conn;
        var bundle = (StatBundle)fire.Data!;
        if (bundle.OfLine)
        {
            if (fire.Source?.Thing is not Item item || LineBundle(item, fire.Stat, ItemsFor(player).Mods) is not { } own) return;
            bundle = own;
        }
        var stats = BundleAt(bundle, fire.Number("roll"), fire.Number("effect"), fire.Number("own"));
        if (stats.Count == 0) return;
        if (to == "self")
        {
            // A timed buff of the actor's own, shown as the bundle says (or not at all).
            var spec = GrantSpecs.GetValue(stats, given => new($"grant:{bundle.Name}", bundle.Shows ?? NoBuffRow, [.. given.Select(s => (s.Key, s.Value))]));
            GainTimedBuffLocked(player, spec, seconds, 1, fire.Now);
            return;
        }
        if (fire.Event.Monster is not { Dead: false, Life: > 0 } m) return;
        var hit = fire.Event as HitLanded;
        var taken = fire.Event as ChargeEvent;
        uint id = hit?.MonsterId ?? taken?.MonsterId ?? 0;
        if (id == 0)
            foreach (var (other, monster) in player.Area.Monsters)
                if (ReferenceEquals(monster, m)) { id = other; break; }
        if (id == 0) return;
        stats = ForMonsters(stats);
        string key = $"fx:{player.Id}:hit:{bundle.Name}";
        double until = (hit?.At ?? taken?.HitAt ?? ProjectileWorldTime(player)) + seconds;
        var set = GrantSets.GetOrCreateValue(player);
        lock (set) set.Timed[(m, key)] = (id, until, bundle.Shows);
        if (!m.TemporaryStats.TryGetValue(key, out var has) || !ReferenceEquals(has, stats)) ChangeGrantLocked(player, id, m, key, stats, bundle.Shows);
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
            // The grants for players: each player of the area has the stats while the rule stands; one who arrives
            // later is given them here, and all of them read their stats again when the rule is gone.
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].ForPlayers is not { } forPlayers) continue;
                var grant = all[i];
                if (on[i])
                {
                    foreach (var other in player.Frames.Players().Append(player).Distinct())
                        if (!other.Closed && !other.LeavingArea && grant.Given.Add(other))
                        {
                            GainStandingStats(other, forPlayers, () => grant.On() && !player.Closed && !player.LeavingArea && !other.LeavingArea);
                            gained[i]++;
                        }
                }
                else
                    foreach (var other in grant.Given)
                        if (!other.Closed && !other.LeavingArea) MarkPlayerStats(other, PlayerStatStep.Sources);
            }
            foreach (var (id, m) in player.Area.Monsters)
            {
                if (m.Dead) continue;   // a corpse keeps what it had
                bool enemy = m.Hostile && m.Life > 0;
                double away = -1;   // worked out once, for the first grant that has a radius
                for (int i = 0; i < all.Length; i++)
                {
                    var grant = all[i];
                    if (grant.ForPlayers is not null) continue;
                    bool want = on[i] && enemy
                        && (grant.Radius <= 0 || (away < 0 ? away = Dist(at, m.Walk.Position(now)) : away) <= grant.Radius);
                    if (want == m.TemporaryStats.ContainsKey(grant.Key)) continue;
                    ChangeGrantLocked(player, id, m, grant.Key, want ? grant.Stats : null, grant.Shows);
                    if (want) gained[i]++;
                }
            }
            for (int i = 0; i < all.Length; i++)
                if (on[i] && !all[i].Told && gained[i] > 0)
                {
                    all[i].Told = true;
                    Console.WriteLine($"[gateway] {player.Connection.Remote} \"{all[i].Name}\" is on {gained[i]} {(all[i].ForPlayers is null ? "monster" : "player")}(s)");
                }
            // A grant whose rule is gone has been taken off above: it leaves the list.
            if (ended)
                lock (set) set.Standing = [.. set.Standing.Where(g => Array.IndexOf(all, g) is var i && (i < 0 || on[i]))];
        }
        lock (set)
        {
            if (set.Timed.Count == 0) return;
            foreach (var (key, (id, until, shows)) in set.Timed.ToArray())
            {
                if (now < until && !key.Monster.Dead && EffectRules.Enabled) continue;
                set.Timed.Remove(key);
                if (!key.Monster.Dead) ChangeGrantLocked(player, id, key.Monster, key.Key, null, shows);
            }
        }
    }

    /// <summary>Puts a granted layer on a monster (or takes it off: <paramref name="stats"/> null). Its maximum life
    /// follows from the frame its stats are built in, as a shrine's aura does it (MonsterAuraEffect), and flat life
    /// ("+# to maximum Life") counts here: it is added before the monster's own percent modifiers.</summary>
    static void ChangeGrantLocked(Player player, uint id, SpawnedMonster m, string key, IReadOnlyDictionary<uint, int>? stats,
        ushort? shows = null, bool tell = true)
    {
        // The buff the bundle shows as, on the monster while it carries it: stated as an aura's is (EnemyDebuffs).
        if (shows is ushort row && tell && !m.Dead && (stats is null ? m.TemporaryStats.ContainsKey(key) : !m.TemporaryStats.ContainsKey(key)))
        {
            if (stats is null) player.Area.Buffs.Clear(id, row, player.Id);
            else player.Area.Buffs.Set(id, new(row, player.Id));
        }
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
                    ChangeGrantLocked(player, id, m, key, null, null, tell: false);
            }
        }
        Forget(player.Area.Monsters);
        Forget(player.Area.DormantMonsters);
    }
}
