using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible (3.21) weapon trees: the nodes a tree may be made of, and the rules that place and allocate them.
/// <para>
/// A tree is one node per layer, layers 1 to 5. Layers 1 to 4 hold ordinary nodes and layer 5 the notable
/// (<c>WeaponPassiveSkills.Type</c> 0 and 1); <c>Type</c> 2 is a vendor-bonus node the client draws as its black "#"
/// placeholder, so it is never placed. A node's tooltip line is its <c>WeaponPassiveSkills.Tier</c>; a generated tree
/// prefers the tier that matches the layer, which is the shape the real trees have.
/// </para>
/// <para>
/// Which nodes a weapon may hold is the mod's own spawn weight against the base's tags (Mods.dat SpawnWeight, the same
/// list item rolling uses): a mace's tree takes the mace and one-hand-weapon nodes, a two-handed one the "2H" nodes,
/// and a bow's the bow nodes. The pool is therefore read per base type rather than per item class.
/// </para>
/// <para>
/// Three rules the client does not enforce but draws by, all measured on the 3.29.3d client
/// (Agents/docs/protocol/13-items.md §2g): a layer with no node ends the tree, so the layers are a run from 1 with no
/// gap; a slot is drawn lit only when it and every slot above it are allocated; and a node's values must be the mod's
/// real ones, because a zero value renders as the client's "#" placeholder. <see cref="Generate"/> therefore always
/// returns a gap-free tree whose allocation is the prefix through its last layer.
/// </para>
/// </summary>
public static class CrucibleTrees
{
    /// <summary>The last (notable) layer of every tree.</summary>
    public const int LastLayer = 5;

    /// <summary>A WeaponPassiveSkills row with the Mods row its stats come from: one node a tree may hold.</summary>
    /// <param name="Mod">The Mods row: its HASH16 identifies the node and its stats are what the node does.</param>
    /// <param name="Tier">WeaponPassiveSkills.Tier: the tooltip's "等阶".</param>
    /// <param name="Layers">WeaponPassiveSkills.NodeSpawnLocation: the layers the node may sit at.</param>
    /// <param name="Type">WeaponPassiveSkills.Type: 0 ordinary, 1 notable (2, the vendor bonus, is not loaded).</param>
    public sealed record Node(ModCatalog.Mod Mod, int Tier, IReadOnlyList<int> Layers, int Type)
    {
        public string Id => Mod.Id;
        public ushort Hash => Mod.Hash;

        /// <summary>The node's values, in stats order: a generation-31/32 row always has <c>StatNMin == StatNMax</c>, so a
        /// node's values are fixed rather than rolled.</summary>
        public IReadOnlyList<int> Values { get; } = [.. Mod.Stats.Select(stat => stat.Min)];

        /// <summary>Whether the node may sit at this layer.</summary>
        public bool SpawnsOn(int layer) => Layers.Contains(layer);

        /// <summary>Whether a base with these tags may hold the node: its spawn weight, which a tag with weight 0 vetoes.</summary>
        public bool Fits(IReadOnlySet<string> tags) => Mod.WeightFor(tags) > 0;
    }

    static IReadOnlyList<Node> nodes = [];
    static readonly Dictionary<ushort, Node> byHash = [];
    static readonly Dictionary<string, Node> byId = new(StringComparer.OrdinalIgnoreCase);
    // The pool a layer draws from, by layer and type: a tree asks for one per layer.
    static readonly Dictionary<(int Layer, int Type), List<Node>> pools = [];
    static readonly Dictionary<int, uint> bars = [];

    /// <summary>Every node a tree may hold, in table order.</summary>
    public static IReadOnlyList<Node> Nodes => nodes;

    /// <summary>Reads the node pool and the per-item-level unlock bars from client.db. Called once by the module's load;
    /// every list is replaced, so a reload picks up new tables.</summary>
    public static void LoadGameData()
    {
        List<Node> loaded = [];
        var mods = Program.Items.Mods;
        if (ClientTables.Has("WeaponPassiveSkills"))
        {
            var table = ClientTables.Table("Mods");
            foreach (var skill in ClientTables.Table("WeaponPassiveSkills"))
            {
                // Type 2 is the vendor-bonus node: the client draws it as a black "#" and never allocates it.
                if (skill.Int("Type") == 2) continue;
                // GameMode 2 rows are the Ruthless set (0 is every league, 1 not Ruthless): their values come from the
                // HardMode catalog, which this module does not read, so they are left out rather than mispriced.
                if (table.Row(skill.Int("Mod")) is not { } raw || raw.IntOrNull("GameMode") is 2) continue;
                if (mods.Row(skill.Int("Mod")) is not { } mod) continue;
                // GenerationType 31 is the tree's own; 32 is the per-attribute conversion nodes, ordinary nodes too.
                if (mod.GenerationType is not (31 or 32)) continue;
                var layers = ParseLayers(skill.Ints("NodeSpawnLocation"));
                if (layers.Count == 0 || mod.Stats.Count == 0) continue;
                // A node that grants a passive is a PassiveSkills row the server would have to grant (the Crucible
                // "configured" nodes); it does nothing here, so it is left out rather than placed as an empty node.
                if (mod.Stats.Any(stat => stat.Name == "local_weapon_passive_tree_granted_passive_hash")) continue;
                // A node with only zero values would draw as the client's "#" placeholder.
                if (mod.Stats.All(stat => stat.Min == 0 && stat.Max == 0)) continue;

                loaded.Add(new Node(mod, skill.Int("Tier"), layers, skill.Int("Type")));
            }
        }

        Dictionary<int, uint> unlock = [];
        foreach (var row in ClientTables.TableOrEmpty("WeaponPassiveTreeBalancePerItemLevel"))
            if (row.IntOrNull("Level") is { } level) unlock[level] = row.UInt("Bar5");

        nodes = loaded;
        byHash.Clear();
        byId.Clear();
        pools.Clear();
        foreach (var node in loaded)
        {
            byHash.TryAdd(node.Hash, node);
            byId.TryAdd(node.Id, node);
            foreach (int layer in node.Layers)
            {
                if (!pools.TryGetValue((layer, node.Type), out var pool)) pools[(layer, node.Type)] = pool = [];
                pool.Add(node);
            }
        }
        bars.Clear();
        foreach (var (level, bar) in unlock) bars[level] = bar;
    }

    /// <summary>NodeSpawnLocation is a list column ("[3,4]"): the layers the node may sit at, in order.</summary>
    static List<int> ParseLayers(int[] location)
    {
        List<int> layers = [];
        foreach (int layer in location)
            if (layer is >= 1 and <= LastLayer && !layers.Contains(layer))
                layers.Add(layer);
        layers.Sort();
        return layers;
    }

    /// <summary>The node with this Id, or null.</summary>
    public static Node? NodeById(string id) => byId.GetValueOrDefault(id);

    /// <summary>The node with this HASH16, or null.</summary>
    public static Node? NodeByHash(ushort hash) => byHash.GetValueOrDefault(hash);

    /// <summary>The nodes this base may hold on a layer, in table order: its pool after the spawn weights.
    /// Empty for a base that carries no trees.</summary>
    public static IReadOnlyList<Node> Candidates(ItemCatalog.BaseType type, int layer)
    {
        if (layer is < 1 or > LastLayer || !pools.TryGetValue((layer, TypeOf(layer)), out var all)) return [];
        return [.. all.FindAll(node => node.Fits(type.Tags))];
    }

    /// <summary>Whether this base may carry a tree: some layer-1 node takes its tags. Weapons and shields do, the rest
    /// of the bases do not.</summary>
    public static bool CanHaveTree(ItemCatalog.BaseType type) =>
        pools.TryGetValue((1, TypeOf(1)), out var pool) && pool.Exists(node => node.Fits(type.Tags));

    /// <summary>The progress that unlocks every layer at this item level: the row's Bar5. A tree's
    /// <see cref="WeaponTree.Progress"/> is measured in these units, and a layer unlocks at its own bar.</summary>
    public static uint Progress(int itemLevel)
    {
        if (bars.TryGetValue(Math.Clamp(itemLevel, 1, 100), out uint bar)) return bar;
        return bars.Count == 0 ? 0 : bars.Values.Max();
    }

    /// <summary>The slot a layer's first node sits in: the client lays the tree out as a triangle, layer <c>L</c> having
    /// <c>L</c> slots, so the flat index of the layer's first slot is <c>L(L-1)/2</c> (0, 1, 3, 6, 10).</summary>
    public static int Slot(int layer) => layer * (layer - 1) / 2;

    /// <summary>The node type a layer holds: the notable on layer 5, an ordinary node above it.</summary>
    public static int TypeOf(int layer) => layer == LastLayer ? 1 : 0;

    /// <summary>A fresh tree for this base and level: one node per layer, all five allocated.
    /// <paramref name="picks"/> place a chosen node on a layer; the tree is as long as the deepest pick and every layer
    /// above it that is left out is filled with a real node, so a pick on layer 3 gives a three-layer tree allocated
    /// through layer 3.</summary>
    /// <exception cref="ArgumentException">The base has no trees, a pick names no node, or the node cannot sit at that
    /// layer or in this base's pool.</exception>
    public static WeaponTree Generate(ItemCatalog.BaseType type, int itemLevel, Random random, params (int Layer, string Node)[] picks)
    {
        if (!CanHaveTree(type))
            throw new ArgumentException($"a {type.Class} does not carry a Crucible tree", nameof(type));

        Dictionary<int, Node> placed = [];
        foreach (var (layer, id) in picks)
        {
            if (layer is < 1 or > LastLayer)
                throw new ArgumentException($"a tree has layers 1 to {LastLayer}, not {layer}", nameof(picks));
            var node = NodeById(id) ?? throw new ArgumentException($"no weapon-tree node {id}", nameof(picks));
            if (!node.SpawnsOn(layer))
                throw new ArgumentException($"{id} does not spawn on layer {layer}", nameof(picks));
            if (node.Type != TypeOf(layer))
                throw new ArgumentException($"{id} is Type {node.Type}; layer {layer} holds Type {TypeOf(layer)}", nameof(picks));
            if (!node.Fits(type.Tags))
                throw new ArgumentException($"{id} does not belong to a {type.Class}'s pool", nameof(picks));
            placed[layer] = node;
        }

        int top = placed.Count == 0 ? LastLayer : placed.Keys.Max();
        for (int layer = 1; layer <= top; layer++)
            placed.TryAdd(layer, Choose(type, layer, random));

        List<ItemMod> mods = [];
        int mask = 0;
        for (int layer = 1; layer <= top; layer++)
        {
            mask |= 1 << Slot(layer);
            mods.Add(new ItemMod(ModSection.Implicit, placed[layer].Hash, placed[layer].Values));
        }
        return new WeaponTree
        {
            BalanceRow = (uint)Math.Clamp(itemLevel, 1, 100),
            Progress = Progress(itemLevel),
            NodeMask = (ushort)mask,
            AllocatedMask = (ushort)mask,
            TierPicks = Picks(top),
            Nodes = mods,
        };
    }

    /// <summary>The same tree with its allocation moved to the prefix through <paramref name="throughLayer"/>: the shape
    /// a player's picks make, since the client lights a slot only when it and every slot above it are allocated.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="throughLayer"/> is outside 0..5.</exception>
    /// <exception cref="ArgumentException">The tree has no node that deep, so the prefix would not be gap-free.</exception>
    public static WeaponTree Allocate(WeaponTree tree, int throughLayer)
    {
        if (throughLayer is < 0 or > LastLayer)
            throw new ArgumentOutOfRangeException(nameof(throughLayer), throughLayer, $"a tree has layers 1 to {LastLayer}");
        if (throughLayer > 0 && (tree.NodeMask & (1 << Slot(throughLayer))) == 0)
            throw new ArgumentException($"the tree has no node on layer {throughLayer}", nameof(throughLayer));

        int mask = 0;
        for (int layer = 1; layer <= throughLayer; layer++) mask |= 1 << Slot(layer);
        return tree with { AllocatedMask = (ushort)mask, TierPicks = Picks(throughLayer) };
    }

    /// <summary>One pick per layer: slot 0 of every allocated layer, none for the rest.</summary>
    static IReadOnlyList<byte> Picks(int top)
    {
        byte[] picks = new byte[LastLayer];
        for (int i = 0; i < LastLayer; i++) picks[i] = i < top ? (byte)0 : (byte)0xff;
        return picks;
    }

    /// <summary>A node for this layer of the base's pool, drawn at random: the nodes that spawn only on this layer
    /// first, then the tier that matches the layer.</summary>
    static Node Choose(ItemCatalog.BaseType type, int layer, Random random)
    {
        if (!pools.TryGetValue((layer, TypeOf(layer)), out var all))
            throw new InvalidOperationException($"no node for layer {layer}");

        List<Node> pool = all.FindAll(node => node.Fits(type.Tags));
        if (pool.Count == 0) throw new InvalidOperationException($"no node for layer {layer} of a {type.Class}");

        var only = pool.FindAll(node => node.Layers.Count == 1);
        if (only.Count > 0) pool = only;

        var sameTier = pool.FindAll(node => node.Tier == layer);
        if (sameTier.Count > 0) pool = sameTier;

        return pool[random.Next(pool.Count)];
    }
}
