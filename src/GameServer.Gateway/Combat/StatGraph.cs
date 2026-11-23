using System.Diagnostics.CodeAnalysis;

/// <summary>
/// One entity's stats, kept current by what changes instead of built again whole.
/// <list type="bullet">
/// <item>A <b>layer</b> is what one source gives (an item set, the passives, a buff list). It is built alone when its owner
/// marks it (<see cref="MarkLayer"/>), and the layers' sums change only for the ids it changed.</item>
/// <item>A <b>rule</b> is a step after the layers, in the order rules were added: it sees the layers' sums with the earlier
/// rules' changes on top, and changes stats in place through a <see cref="StatView"/>, as code on a stat dictionary does.
/// The view records each id the rule read, and the rule runs again only when one of those changes for it (a layer's sum,
/// or an earlier rule's write), or when its owner marks it (<see cref="MarkRule"/>) for state it reads that is not a stat.
/// A run that writes what it wrote before changes nothing further.</item>
/// </list>
/// <item>An <b>input</b> is state a rule reads that is not a stat (a player's pools, items, passives). Its accessor calls
/// <see cref="ReadInput"/>, which records the read for the rule running on this thread; <see cref="MarkInput"/> then runs
/// again exactly the rules that read it.</item>
/// <see cref="Step"/> applies what was marked since the last one; the area's Stats phase calls it once a frame, and the
/// values read in between are the last step's (<see cref="Values"/>).
/// </summary>
public sealed class StatGraph
{
    public delegate void LayerBuild(StatWriter into);
    public delegate void RuleRun(StatView stats);

    sealed class Layer
    {
        public required LayerBuild Build;
        public Dictionary<uint, int> Adds = [];
        public bool Dirty = true;
    }

    internal sealed class Rule
    {
        public required int Index;
        public required RuleRun Run;
        public Dictionary<uint, int?> Writes = [];   // null: the rule removed the id
        public readonly HashSet<uint> Reads = [];
        public readonly HashSet<int> Inputs = [];
        public bool ReadsAll, Queued;
    }

    readonly List<Layer> layers = [];
    readonly List<Rule> rules = [];
    readonly Dictionary<uint, int> totals = [];          // the layers' sums
    readonly Dictionary<uint, int> layerWriters = [];    // how many layers write an id (0 = absent from the sums)
    readonly Dictionary<uint, SortedList<int, int?>> writes = [];   // each id's rule writes, by rule index
    readonly Dictionary<uint, HashSet<Rule>> readers = [];
    readonly HashSet<Rule> allReaders = [];
    readonly Dictionary<int, HashSet<Rule>> inputReaders = [];
    readonly HashSet<uint> changed = [];
    readonly PriorityQueue<Rule, int> queue = new();
    volatile Dictionary<uint, int> values = [];
    bool dirtyLayers;
    // Marks from any thread, taken by the next step: a mark never waits for a step (which holds the graph while its rules
    // take other locks), so a thread holding one of those can mark and read.
    readonly System.Collections.Concurrent.ConcurrentQueue<int> markedLayers = new(), markedRules = new(), markedInputs = new();
    // The view of the rule running on this thread, for ReadInput.
    [ThreadStatic] static StatView? reading;

    /// <summary>The values as of the last step. Never changed after a step published it: hold it as long as needed. Reading
    /// them takes no lock.</summary>
    public IReadOnlyDictionary<uint, int> Values => values;

    /// <summary>The values the rule <paramref name="rule"/> saw in the last step: the layers' sums with the earlier rules'
    /// writes (a panel's sources, the stats before its tail).</summary>
    public Dictionary<uint, int> ValuesBefore(int rule)
    {
        lock (this)
        {
            var before = new Dictionary<uint, int>();
            foreach (uint id in VisibleIds(rule))
                if (Visible(rule, id, out int value)) before[id] = value;
            return before;
        }
    }

    /// <summary>The ids whose value the last step changed (added, removed or a new value).</summary>
    public IReadOnlyCollection<uint> Changed { get; private set; } = [];

    /// <summary>Steps that changed something, layer builds and rule runs, for Game_TRACE=budget.</summary>
    public long Steps, LayerBuilds, RuleRuns;

    /// <summary>A layer, built on the first step and then each time it is marked. Returns what marks it.</summary>
    public int AddLayer(LayerBuild build)
    {
        lock (this)
        {
            layers.Add(new() { Build = build });
            dirtyLayers = true;
            return layers.Count - 1;
        }
    }

    /// <summary>A rule after those added before it, run on the first step. Returns what marks it.</summary>
    public int AddRule(RuleRun run)
    {
        lock (this)
        {
            var rule = new Rule { Index = rules.Count, Run = run };
            rules.Add(rule);
            Queue(rule);
            return rule.Index;
        }
    }

    /// <summary>The layer's source changed: it is built again in the next step.</summary>
    public void MarkLayer(int layer) => markedLayers.Enqueue(layer);

    /// <summary>State the rule reads that is not a stat changed: it runs in the next step, or in this one when a rule
    /// before it changed that state while running.</summary>
    public void MarkRule(int rule)
    {
        if (reading is { } view && view.Precedes(this, rule)) Queue(rules[rule]);
        else markedRules.Enqueue(rule);
    }

    /// <summary>A mark is waiting for the next step (a change made after this one ran).</summary>
    public bool MarksPending => !markedLayers.IsEmpty || !markedRules.IsEmpty || !markedInputs.IsEmpty;

    /// <summary>An input changed: the rules that read it run in the next step.</summary>
    public void MarkInput(int input) => markedInputs.Enqueue(input);

    /// <summary>Whether a rule runs on this thread now (its reads are recorded).</summary>
    public static bool InRule => reading is not null;

    /// <summary>An input's accessor: the rule running on this thread (if any) reads it.</summary>
    public static void ReadInput(int input)
    {
        if (reading is { } view) view.ReadInput(input);
    }

    /// <summary>Applies what was marked since the last step. False when no value changed.</summary>
    public bool Step()
    {
        lock (this)
        {
            while (markedLayers.TryDequeue(out int marked)) { layers[marked].Dirty = true; dirtyLayers = true; }
            while (markedRules.TryDequeue(out int marked)) Queue(rules[marked]);
            while (markedInputs.TryDequeue(out int input))
                if (inputReaders.TryGetValue(input, out var set)) foreach (var r in set) Queue(r);
            if (!dirtyLayers && queue.Count == 0) { Changed = []; return false; }
            if (dirtyLayers)
            {
                dirtyLayers = false;
                foreach (var layer in layers)
                    if (layer.Dirty)
                    {
                        layer.Dirty = false;
                        LayerBuilds++;
                        var into = new StatWriter();
                        layer.Build(into);
                        ApplyLayer(layer, into.Adds);
                    }
            }
            while (queue.TryDequeue(out var rule, out _))
            {
                rule.Queued = false;
                RuleRuns++;
                foreach (uint id in rule.Reads) readers[id].Remove(rule);
                rule.Reads.Clear();
                foreach (int input in rule.Inputs) inputReaders[input].Remove(rule);
                rule.Inputs.Clear();
                if (rule.ReadsAll) { allReaders.Remove(rule); rule.ReadsAll = false; }
                var view = new StatView(this, rule);
                var outer = reading;
                reading = view;
                try { rule.Run(view); }
                finally { view.Done = true; reading = outer; }
                ApplyRule(rule, view.Written);
            }
            return Publish();
        }
    }

    void ApplyLayer(Layer layer, Dictionary<uint, int> adds)
    {
        foreach (var (id, old) in layer.Adds)
            if (!adds.ContainsKey(id))
            {
                int n = layerWriters[id] - 1;
                if (n > 0) layerWriters[id] = n; else layerWriters.Remove(id);
                AddTotal(id, -old, presence: true);
            }
        foreach (var (id, value) in adds)
        {
            bool had = layer.Adds.TryGetValue(id, out int old);
            if (!had) layerWriters[id] = layerWriters.GetValueOrDefault(id) + 1;
            AddTotal(id, value - old, presence: !had);
        }
        layer.Adds = adds;
    }

    void AddTotal(uint id, int delta, bool presence)
    {
        if (delta != 0) totals[id] = (int)Math.Clamp((long)totals.GetValueOrDefault(id) + delta, int.MinValue, int.MaxValue);
        if (delta == 0 && !presence) return;
        if (!layerWriters.ContainsKey(id)) totals.Remove(id);
        Touched(id, after: -1);
    }

    void ApplyRule(Rule rule, Dictionary<uint, int?> written)
    {
        foreach (var (id, _) in rule.Writes)
            if (!written.ContainsKey(id))
            {
                var list = writes[id];
                list.Remove(rule.Index);
                if (list.Count == 0) writes.Remove(id);
                Touched(id, rule.Index);
            }
        foreach (var (id, value) in written)
        {
            if (rule.Writes.TryGetValue(id, out int? old) && old == value) continue;
            if (!writes.TryGetValue(id, out var list)) writes[id] = list = [];
            list[rule.Index] = value;
            Touched(id, rule.Index);
        }
        rule.Writes = written;
    }

    // An id changed for the rules after index `after` (-1: the layers' sums, every rule).
    void Touched(uint id, int after)
    {
        changed.Add(id);
        if (readers.TryGetValue(id, out var set))
            foreach (var r in set) if (r.Index > after) Queue(r);
        foreach (var r in allReaders) if (r.Index > after) Queue(r);
    }

    void Queue(Rule rule)
    {
        if (rule.Queued) return;
        rule.Queued = true;
        queue.Enqueue(rule, rule.Index);
    }

    bool Publish()
    {
        if (changed.Count == 0) { Changed = []; return false; }
        Dictionary<uint, int>? next = null;
        var differs = new List<uint>();
        foreach (uint id in changed)
        {
            bool present = Visible(int.MaxValue, id, out int value);
            bool had = values.TryGetValue(id, out int old);
            if (present == had && (!present || old == value)) continue;
            next ??= new Dictionary<uint, int>(values);
            if (present) next[id] = value; else next.Remove(id);
            differs.Add(id);
        }
        changed.Clear();
        Changed = differs;
        if (next is null) return false;
        values = next;
        Steps++;
        return true;
    }

    // The id as a rule at `index` sees it: the last earlier rule's write, else the layers' sum.
    bool Visible(int index, uint id, out int value)
    {
        if (writes.TryGetValue(id, out var list))
        {
            var keys = list.Keys;
            int lo = 0, hi = keys.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid] < index) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found >= 0)
            {
                int? written = list.Values[found];
                value = written ?? 0;
                return written.HasValue;
            }
        }
        if (layerWriters.ContainsKey(id)) { value = totals.GetValueOrDefault(id); return true; }
        value = 0;
        return false;
    }

    // Every id present for a rule at `index`.
    IEnumerable<uint> VisibleIds(int index)
    {
        foreach (uint id in layerWriters.Keys) if (Visible(index, id, out _)) yield return id;
        foreach (uint id in writes.Keys) if (!layerWriters.ContainsKey(id) && Visible(index, id, out _)) yield return id;
    }

    /// <summary>
    /// A rule's stats while it runs: the stats as the rule sees them, changed in place. Every read is recorded (a read of all
    /// of them, as an enumeration or Count is, makes the rule run again on any change before it); every write is the rule's.
    /// </summary>
    public sealed class StatView : StatMap
    {
        readonly StatGraph graph;
        readonly Rule rule;
        internal readonly Dictionary<uint, int?> Written = [];
        internal bool Done;

        internal StatView(StatGraph graph, Rule rule) { this.graph = graph; this.rule = rule; }

        // The running rule of this graph comes before rule `index` (the step holds the graph's lock on this thread).
        internal bool Precedes(StatGraph g, int index) => !Done && ReferenceEquals(graph, g) && index > rule.Index;

        Rule Live => Done ? throw new InvalidOperationException("a stat rule's view was used after the rule returned") : rule;

        public override bool TryGetValue(uint id, [MaybeNullWhen(false)] out int value)
        {
            var r = Live;
            if (Written.TryGetValue(id, out int? own)) { value = own ?? 0; return own.HasValue; }
            if (r.Reads.Add(id))
            {
                if (!graph.readers.TryGetValue(id, out var set)) graph.readers[id] = set = [];
                set.Add(r);
            }
            return graph.Visible(r.Index, id, out value);
        }

        internal void ReadInput(int input)
        {
            var r = Live;
            if (!r.Inputs.Add(input)) return;
            if (!graph.inputReaders.TryGetValue(input, out var set)) graph.inputReaders[input] = set = [];
            set.Add(r);
        }

        protected override void Set(uint id, int value) { _ = Live; Written[id] = value; }

        public override bool Remove(uint id)
        {
            bool had = ContainsKey(id);
            Written[id] = null;
            return had;
        }

        protected override List<KeyValuePair<uint, int>> Pairs()
        {
            var r = Live;
            if (!r.ReadsAll) { r.ReadsAll = true; graph.allReaders.Add(r); }
            var ids = new HashSet<uint>(graph.VisibleIds(r.Index));
            foreach (var (id, own) in Written) if (own.HasValue) ids.Add(id); else ids.Remove(id);
            var pairs = new List<KeyValuePair<uint, int>>(ids.Count);
            foreach (uint id in ids)
                pairs.Add(new(id, Written.TryGetValue(id, out int? own) ? own!.Value
                    : graph.Visible(r.Index, id, out int v) ? v : 0));
            return pairs;
        }
    }
}

/// <summary>What a layer gives: its sums, by id. A stat written as 0 is present at 0.</summary>
public sealed class StatWriter
{
    internal readonly Dictionary<uint, int> Adds = [];

    /// <summary>Adds to the id's sum (twice adds twice).</summary>
    public void Add(uint id, int value) =>
        Adds[id] = (int)Math.Clamp((long)Adds.GetValueOrDefault(id) + value, int.MinValue, int.MaxValue);
}
