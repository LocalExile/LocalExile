using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>Split Personality's socket effect along the shortest allocated path, including nested cluster rings.</summary>
public static class JewelPathEffects
{
    public const string EffectStat = "local_jewel_+%_effect_per_passive_between_jewel_and_class_start";
    const int Expansion = 1 << 16;
    static readonly Lazy<Dictionary<AscendancyFamily, Dictionary<ushort, HashSet<ushort>>>> Graphs = new(() =>
        Enum.GetValues<AscendancyFamily>().ToDictionary(f => f, PassiveGraphs.Links));
    static readonly Lazy<Dictionary<int, ushort>> Starts = new(() => ClientTables.Table("PassiveSkills")
        .SelectMany(p => p.Ints("Characters").Select(c => (Class: ClientTables.Table("Characters")[c].Int("IntegerId"),
            Node: checked((ushort)p.Int("PassiveSkillGraphId")))))
        .ToDictionary(p => p.Class, p => p.Node));

    sealed record Cache(PassiveTree Tree, ItemCatalog Catalog, AscendancyFamily Family, int Class,
        ushort[] Nodes, ushort[] Clusters, InventoryItem[] Jewels, Dictionary<int, int> Counts)
    {
        public bool Matches(PassiveTree tree, ItemCatalog catalog, AscendancyFamily family, int cls,
            ushort[] nodes, ushort[] clusters, IReadOnlyList<InventoryItem> jewels) =>
            ReferenceEquals(Tree, tree) && ReferenceEquals(Catalog, catalog) && Family == family && Class == cls
            && Nodes.AsSpan().SequenceEqual(nodes) && Clusters.AsSpan().SequenceEqual(clusters)
            && Jewels.Length == jewels.Count && Jewels.Zip(jewels).All(p => p.First.Id == p.Second.Id
                && p.First.X == p.Second.X && ReferenceEquals(p.First.Item, p.Second.Item));
    }
    static readonly RuleTable<ClientState, Cache> Caches = new();

    public static List<InventoryItem> Apply(ClientState conn, PassiveTree tree, ItemCatalog catalog,
        AscendancyFamily family, ushort[] nodes, ushort[] clusters, IReadOnlyList<InventoryItem> jewels,
        List<InventoryItem> active)
    {
        var targets = active.Select(j => (Jewel: j, Percent: ItemModStats.Local(j.Item, catalog.Mods).GetValueOrDefault(EffectStat)))
            .Where(j => j.Percent != 0).ToArray();
        if (targets.Length == 0) return active;
        int cls = Program.Skills.ClassFor(conn.ClassRow).IntegerId;
        if (!Caches.TryGetValue(conn, out var cache) || !cache.Matches(tree, catalog, family, cls, nodes, clusters, jewels))
        {
            var counts = Counts(tree, family, cls, nodes, clusters, jewels);
            cache = new(tree, catalog, family, cls, [.. nodes], [.. clusters], [.. jewels], counts);
            Caches.AddOrUpdate(conn, cache);
        }
        var scaled = targets.ToDictionary(t => t.Jewel.Id, t => Scale(t.Jewel.Item, catalog.Mods,
            (long)t.Percent * cache.Counts.GetValueOrDefault(t.Jewel.X)));
        return [.. active.Select(j => scaled.TryGetValue(j.Id, out var item) ? j with { Item = item } : j)];
    }

    public static Dictionary<int, int> Counts(PassiveTree tree, AscendancyFamily family, int cls,
        IEnumerable<ushort> ordinary, IEnumerable<ushort> clusters, IReadOnlyList<InventoryItem> jewels)
    {
        var result = new Dictionary<int, int>();
        if (!Starts.Value.TryGetValue(cls, out ushort start)) return result;
        var allocated = ordinary.Where(n => tree.Find(n) is { IsAscendancy: false, IsMastery: false,
            IsProxy: false, IsExpansion: false }).Select(n => (int)n).ToHashSet();
        allocated.Add(start);
        allocated.UnionWith(clusters.Select(n => Expansion + n));
        // Copy only the allocated ordinary subgraph; never bridge an unallocated node or mastery.
        var graph = new Dictionary<int, HashSet<int>>();
        void Edge(int a, int b)
        {
            if (!allocated.Contains(a) || !allocated.Contains(b)) return;
            if (!graph.TryGetValue(a, out var x)) graph[a] = x = [];
            if (!graph.TryGetValue(b, out var y)) graph[b] = y = [];
            x.Add(b); y.Add(a);
        }
        foreach (int n in allocated.Where(n => n < Expansion))
            foreach (ushort adjacent in Graphs.Value[family].GetValueOrDefault((ushort)n) ?? []) Edge(n, adjacent);
        var layouts = jewels.Select(j => (j.X, Layout: ClusterJewelLayout.For(j.X, j.Item)))
            .Where(p => p.Layout is not null).ToDictionary(p => (int)p.X, p => p.Layout!);
        foreach (var (cell, layout) in layouts)
        {
            int[] ring = layout.ByOffset.Keys.Order().ToArray();
            for (int i = 0; i < ring.Length; i++)
                if (ring.Length > 1) Edge(Expansion + layout.Base + ring[i], Expansion + layout.Base + ring[(i + 1) % ring.Length]);
            int? parent = tree.JewelNodeForSlot(cell) is ushort root ? root
                : tree.ClusterParent(cell) is int up && layouts.TryGetValue(up, out var parentLayout)
                    && parentLayout.ChildSockets.TryGetValue(cell, out int offset) ? Expansion + parentLayout.Base + offset : null;
            if (parent is int from) Edge(from, Expansion + layout.Base + layout.EntryOffset);
        }
        var distance = new Dictionary<int, int> { [start] = 0 };
        var queue = new Queue<int>(); queue.Enqueue(start);
        while (queue.TryDequeue(out int n))
            foreach (int next in graph.GetValueOrDefault(n) ?? [])
                if (distance.TryAdd(next, distance[n] + 1)) queue.Enqueue(next);
        foreach (var jewel in jewels)
        {
            int? socket = tree.JewelNodeForSlot(jewel.X) is ushort root ? root
                : tree.ClusterParent(jewel.X) is int up && layouts.TryGetValue(up, out var layout)
                    && layout.ChildSockets.TryGetValue(jewel.X, out int offset) ? Expansion + layout.Base + offset : null;
            // The class start and the socket containing this jewel are not passives between the two endpoints.
            if (socket is int node && distance.TryGetValue(node, out int length)) result[jewel.X] = Math.Max(0, length - 1);
        }
        return result;
    }

    static Item Scale(Item item, ModCatalog mods, long percent) => percent == 0 ? item : item with
    {
        Mods = [.. item.Mods.Select(m => mods.ByHash(m.Hash) is { } row ? m with
        {
            Values = [.. m.Values.Select((v, i) => i < row.Stats.Count && !row.Stats[i].Local
                ? (int)Math.Clamp(decimal.Floor((decimal)v * (100m + percent) / 100m), int.MinValue, int.MaxValue) : v)]
        } : m)],
        RequiredLevel = item.RequiredLevel
    };
}
