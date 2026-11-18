/// <summary>
/// One entity's stats, kept current by what changes instead of built again whole.
/// <list type="bullet">
/// <item>A <b>layer</b> is what one source gives (an item set, the passives, a buff list). It is built alone when its owner
/// marks it (<see cref="Mark"/>), and only the ids whose total changed are added up again.</item>
/// <item>An <b>input</b> is state a rule needs that is not a stat (a condition, a count): its owner sets it
/// (<see cref="SetInput"/>), under an id of its own (<see cref="InputId"/>).</item>
/// <item>A <b>rule</b> is a derived stat. It reads through <see cref="StatReader"/>, which records each id it read, and runs
/// again only when one of those changed. It adds to ids (<see cref="StatWriter.Add"/>) or owns them
/// (<see cref="StatWriter.Set"/>: the id's value is the rule's, which may read the id's total without it by
/// <see cref="StatReader.Total"/>). A change that leaves a value as it was goes no further.</item>
/// </list>
/// <see cref="Step"/> applies what was marked or set since the last one; the area's Stats phase calls it once a frame, and
/// the values read in between are the last step's. Rules run in the order they were added, which is the order they read
/// each other's results in; one that reads a later one's result runs again after it.
/// </summary>
public sealed class StatGraph
{
    public delegate void LayerBuild(StatWriter into);
    public delegate void RuleRun(StatReader read, StatWriter write);

    /// <summary>Ids from here up are inputs, never a client stat.</summary>
    public const uint FirstInput = 0x8000_0000;
    public static uint InputId(int input) => FirstInput + (uint)input;

    internal sealed class Source
    {
        public required int Index;
        public LayerBuild? Build;
        public RuleRun? Run;
        public Dictionary<uint, int> Adds = [];
        public Dictionary<uint, int> Sets = [];
        public HashSet<uint> TotalReads = [], ValueReads = [];
        public bool Dirty = true, Queued;
    }

    readonly List<Source> layers = [];
    readonly List<Source> rules = [];
    readonly Dictionary<uint, int> totals = [];       // every layer's and rule's Add, plus the inputs
    readonly Dictionary<uint, int> writers = [];      // how many layers, rules and inputs write an id: 0 = absent
    readonly Dictionary<uint, Source> owners = [];    // the rule that Sets an id
    readonly Dictionary<uint, int> inputs = [];
    readonly Dictionary<uint, List<Source>> totalReaders = [], valueReaders = [];
    readonly HashSet<uint> changedTotals = [];
    readonly HashSet<uint> changedValues = [];
    readonly PriorityQueue<Source, int> queue = new();
    Dictionary<uint, int> values = [];
    bool dirtyLayers;

    /// <summary>The values as of the last step. Never changed after a step published it: hold it as long as needed.</summary>
    public IReadOnlyDictionary<uint, int> Values { get { lock (this) return values; } }

    /// <summary>The ids whose value the last step changed (added, removed or a new value).</summary>
    public IReadOnlyCollection<uint> Changed { get; private set; } = [];

    /// <summary>Steps taken and rule runs, for Game_TRACE=budget.</summary>
    public long Steps, LayerBuilds, RuleRuns;

    /// <summary>A layer, built on its first step and then each time it is marked. The returned index marks it.</summary>
    public int AddLayer(LayerBuild build)
    {
        lock (this)
        {
            layers.Add(new() { Index = layers.Count, Build = build });
            dirtyLayers = true;
            return layers.Count - 1;
        }
    }

    /// <summary>A rule, run on the first step and then when an id it read changes. Add them in the order they read each other.</summary>
    public void AddRule(RuleRun run)
    {
        lock (this)
        {
            var rule = new Source { Index = rules.Count, Run = run };
            rules.Add(rule);
            Queue(rule);
        }
    }

    /// <summary>The layer's source changed: it is built again in the next step.</summary>
    public void Mark(int layer)
    {
        lock (this) { layers[layer].Dirty = true; dirtyLayers = true; }
    }

    /// <summary>An input's new value; a rule that read it runs in the next step if it differs.</summary>
    public void SetInput(uint id, int value)
    {
        lock (this)
        {
            bool had = inputs.TryGetValue(id, out int old);
            if (had && old == value) return;
            inputs[id] = value;
            if (!had) writers[id] = writers.GetValueOrDefault(id) + 1;
            AddTotal(id, value - old);
        }
    }

    /// <summary>Applies the marks and inputs since the last step. False when no value changed.</summary>
    public bool Step()
    {
        lock (this)
        {
            if (!dirtyLayers && changedTotals.Count == 0 && queue.Count == 0) { Changed = []; return false; }
            Steps++;
            if (dirtyLayers)
            {
                dirtyLayers = false;
                foreach (var layer in layers)
                    if (layer.Dirty)
                    {
                        layer.Dirty = false;
                        LayerBuilds++;
                        var w = new StatWriter();
                        layer.Build!(w);
                        Apply(layer, w);
                    }
            }
            int runs = 0, limit = 16 * (rules.Count + 1);
            while (true)
            {
                Settle();
                if (!queue.TryDequeue(out var rule, out _)) break;
                rule.Queued = false;
                if (++runs > limit) throw new InvalidOperationException("stat rules keep changing each other: a cycle");
                RuleRuns++;
                foreach (uint id in rule.TotalReads) totalReaders[id].Remove(rule);
                foreach (uint id in rule.ValueReads) valueReaders[id].Remove(rule);
                rule.TotalReads.Clear();
                rule.ValueReads.Clear();
                var read = new StatReader(this, rule);
                var w = new StatWriter();
                rule.Run!(read, w);
                read.Done = true;
                Apply(rule, w);
            }
            if (changedValues.Count == 0) { Changed = []; return false; }
            var next = new Dictionary<uint, int>(values);
            foreach (uint id in changedValues)
                if (writers.GetValueOrDefault(id) > 0) next[id] = Value(id);
                else next.Remove(id);
            values = next;
            Changed = changedValues.ToArray();
            changedValues.Clear();
            return true;
        }
    }

    // A source's new writes against its last ones.
    void Apply(Source source, StatWriter w)
    {
        foreach (var (id, old) in source.Adds)
            if (!w.Adds.ContainsKey(id)) { Unwrite(id); AddTotal(id, -old); }
        foreach (var (id, value) in w.Adds)
        {
            if (!source.Adds.TryGetValue(id, out int old)) { writers[id] = writers.GetValueOrDefault(id) + 1; changedValues.Add(id); }
            AddTotal(id, value - old);
        }
        source.Adds = w.Adds;
        foreach (var (id, _) in source.Sets)
            if (!w.Sets.ContainsKey(id)) { owners.Remove(id); Unwrite(id); TotalOrValueChanged(id); }
        foreach (var (id, value) in w.Sets)
        {
            if (!source.Sets.TryGetValue(id, out int old))
            {
                if (owners.TryGetValue(id, out var other) && other != source)
                    throw new InvalidOperationException($"stat {id} is set by two rules");
                owners[id] = source;
                writers[id] = writers.GetValueOrDefault(id) + 1;
                TotalOrValueChanged(id);
            }
            else if (old != value) ValueChanged(id);
        }
        source.Sets = w.Sets;
    }

    void Unwrite(uint id)
    {
        int n = writers.GetValueOrDefault(id) - 1;
        if (n > 0) writers[id] = n; else writers.Remove(id);
        changedValues.Add(id);
    }

    void AddTotal(uint id, int delta)
    {
        if (delta == 0) return;
        totals[id] = (int)Math.Clamp((long)totals.GetValueOrDefault(id) + delta, int.MinValue, int.MaxValue);
        changedTotals.Add(id);
    }

    void TotalOrValueChanged(uint id) { changedTotals.Add(id); ValueChanged(id); }

    void ValueChanged(uint id)
    {
        changedValues.Add(id);
        if (valueReaders.TryGetValue(id, out var readers)) foreach (var r in readers) Queue(r);
    }

    // The totals that changed: the rules that read them, and the values they are (an id no rule owns).
    void Settle()
    {
        if (changedTotals.Count == 0) return;
        foreach (uint id in changedTotals)
        {
            if (totalReaders.TryGetValue(id, out var readers)) foreach (var r in readers) Queue(r);
            if (!owners.ContainsKey(id)) ValueChanged(id);
        }
        changedTotals.Clear();
    }

    void Queue(Source rule)
    {
        if (rule.Queued) return;
        rule.Queued = true;
        queue.Enqueue(rule, rule.Index);
    }

    int Value(uint id) => owners.TryGetValue(id, out var owner) ? owner.Sets[id] : totals.GetValueOrDefault(id);

    int ReadValue(Source rule, uint id)
    {
        if (rule.ValueReads.Add(id)) Readers(valueReaders, id).Add(rule);
        return Value(id);
    }

    int ReadTotal(Source rule, uint id)
    {
        if (rule.TotalReads.Add(id)) Readers(totalReaders, id).Add(rule);
        return totals.GetValueOrDefault(id);
    }

    bool ReadHas(Source rule, uint id)
    {
        if (rule.ValueReads.Add(id)) Readers(valueReaders, id).Add(rule);
        return writers.GetValueOrDefault(id) > 0;
    }

    static List<Source> Readers(Dictionary<uint, List<Source>> map, uint id)
    {
        if (!map.TryGetValue(id, out var list)) map[id] = list = [];
        return list;
    }

    /// <summary>What a rule reads: each id is remembered, and the rule runs again when it changes. Only during the run.</summary>
    public sealed class StatReader
    {
        readonly StatGraph graph;
        readonly Source rule;
        internal bool Done;
        internal StatReader(StatGraph graph, Source rule) { this.graph = graph; this.rule = rule; }
        Source Rule => Done ? throw new InvalidOperationException("a stat rule's reader read after the rule returned") : rule;

        /// <summary>The id's value (0 when nothing writes it).</summary>
        public int this[uint id] => graph.ReadValue(Rule, id);
        /// <summary>Whether a layer, rule or input writes the id (a stat present at 0 counts).</summary>
        public bool Has(uint id) => graph.ReadHas(Rule, id);
        /// <summary>The layers', inputs' and adding rules' sum for the id, without the rule that owns it.</summary>
        public int Total(uint id) => graph.ReadTotal(Rule, id);
        /// <summary>An input's value.</summary>
        public int Input(int input) => graph.ReadValue(Rule, InputId(input));
    }
}

/// <summary>What a layer or rule gives. A stat written as 0 is present at 0.</summary>
public sealed class StatWriter
{
    internal readonly Dictionary<uint, int> Adds = [];
    internal readonly Dictionary<uint, int> Sets = [];

    /// <summary>Adds to the id's total (twice adds twice).</summary>
    public void Add(uint id, int value) =>
        Adds[id] = (int)Math.Clamp((long)Adds.GetValueOrDefault(id) + value, int.MinValue, int.MaxValue);

    /// <summary>A rule's own value for the id, whatever the others add (one rule per id).</summary>
    public void Set(uint id, int value) => Sets[id] = value;
}
