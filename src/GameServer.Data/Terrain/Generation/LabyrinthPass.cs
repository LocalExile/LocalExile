namespace GameServer.Data.Terrain.Generation;

/// <summary>The labyrinth node the server sends in an area's extra data (<c>0x141ec7220</c> flag 0x08: u32 seed, u16
/// node, u8 difficulty): the day's layout (<see cref="LabyrinthLayout"/>) and the room's LabyrinthSectionLayout row.</summary>
public sealed record LabyrinthNodeKey(uint Seed, int Row, int Difficulty);

/// <summary><c>0x142032760</c> (.dgr, from <c>0x14203c900</c>) / <c>0x142033e30</c> (.tgr, from <c>0x14203f890</c>): the
/// Labyrinth's graph pass, for an area whose WorldAreas row has +0x171 set (<see cref="AreaInfo.IsLabyrinth"/>), after
/// the tag choices and side areas and before the description pass (<c>0x142034e60</c>). With a labyrinth node
/// (instance +0x48, <paramref name="node"/>) the node's LabyrinthNodeOverrides apply and its two secrets take secret
/// rooms; without one, a boss area builds the seed-0 layout for its override and a door area may take the gold door.</summary>
sealed class LabyrinthPass(Func<string, byte[]?> read, string areaId, bool boss, LabyrinthNodeKey? node = null)
{
    /// <summary>Runs the pass over the graph's nodes. Every node tagged "secret" or "secret" plus one character
    /// (<c>0x14203a2e0</c>) is collected and loses its exist chance (+0x44 = 0). Then the node's overrides and secrets,
    /// else a boss area's override row, else an area named "_quad_door" or "_branch_door" turns the first collected node
    /// into the LabyrinthSecrets "GoldDoor" room. The nodes still collected then also lose their descriptions.</summary>
    public void Run(TileGraph graph, LevelRandom rng)
    {
        var secrets = new List<GraphNode>();
        bool plain = true;
        foreach (var n in graph.Nodes)
        {
            if (n.Tag.Length is not (6 or 7) || !n.Tag.StartsWith("secret", StringComparison.Ordinal)) continue;
            secrets.Add(n);
            n.Exist = 0;
            plain &= n.Tag.Length < 7;
        }
        if (node is not null && Current() is var (layout, current))
        {
            // The node's LabyrinthSectionLayout overrides (+0x5c), each a LabyrinthNodeOverrides row.
            foreach (int row in Table("LabyrinthSectionLayout").RefList(current.Row, 0x5c))
                Override(graph, row, layout, current.Row);
            if (current.Secret1 >= 0 || current.Secret2 >= 0)
            {
                Order(secrets, plain, rng);
                int entrances = 0;
                Give(secrets, current.Secret1, "secret1", current, ref entrances);
                Give(secrets, current.Secret2, "secret2", current, ref entrances);
            }
        }
        else if (boss)
        {
            // 0x1424e5ca0 builds the area's labyrinth with seed 0 (difficulty: the id's first character, 'E' = 4) for the
            // pick below; "_end" areas take the final boss row (Normal: the one without traps), the others Boss1_Sec1.
            int difficulty = areaId[0] == 'E' ? 4 : areaId[0] - '0';
            string id = areaId.Contains("_end", StringComparison.Ordinal) ? difficulty == 1 ? "FinalBossEasy" : "FinalBoss" : "Boss1_Sec1";
            var table = Table("LabyrinthNodeOverrides");
            int row = table.Find(r => table.String(r, 0) == id);
            var seedZero = LabyrinthLayout.Build(read, 0, difficulty);
            if (row >= 0) Override(graph, row, seedZero, seedZero.Nodes[^1].Row);
        }
        else if (areaId.Contains("_quad_door", StringComparison.Ordinal) || areaId.Contains("_branch_door", StringComparison.Ordinal))
            GoldDoor(secrets, plain, rng);
        foreach (var n in secrets)
        {
            n.Exist = 0;
            n.Descriptions.Clear();
        }
    }

    // The day's layout and the node whose LabyrinthSectionLayout row the server sent (0x1424e5ca0: none matches, no node).
    (LabyrinthLayout Layout, LabyrinthLayout.Node Node)? Current()
    {
        var layout = LabyrinthLayout.Build(read, node!.Seed, node.Difficulty);
        return layout.Nodes.FirstOrDefault(n => n.Row == node.Row) is { } current ? (layout, current) : null;
    }

    /// <summary><c>0x142034d30</c> → <c>0x142033ad0</c>: every node whose tag is the LabyrinthNodeOverrides row's node name
    /// (+8) takes one of its overrides (+0x10): none empties the tag, one is taken as it is; with several,
    /// <c>0x1424e5ee0</c> picks with a TinyMT seeded from (difficulty + 4 × the layout node's LabyrinthSectionLayout row)
    /// × 0x13d + the layout's seed (no level draw).</summary>
    void Override(TileGraph graph, int row, LabyrinthLayout layout, int layoutRow)
    {
        var table = Table("LabyrinthNodeOverrides");
        string name = table.String(row, 8);
        if (name.Length == 0) return;
        var overrides = table.StringList(row, 0x10);
        foreach (var n in graph.Nodes)
        {
            if (n.Tag != name) continue;
            int pick = overrides.Length < 2 ? 0
                : (int)new TinyMt((uint)((layout.Difficulty + 4 * layoutRow) * 0x13d + layout.Seed)).Pick((uint)overrides.Length);
            n.Tag = overrides.Length == 0 ? "" : overrides[pick];
        }
    }

    /// <summary>The collected nodes' order before a secret takes the first: shuffled when all are tagged "secret" (one
    /// <c>Next(count)</c>, <c>0x141ff9720</c>, then per place a <c>Uniform</c> draw below the places left, swapped with
    /// the front: the client trace's n(2), u(2), u(1)) or else sorted by tag (<c>0x1420393f0</c>, stable).</summary>
    static void Order(List<GraphNode> secrets, bool plain, LevelRandom rng)
    {
        if (plain)
        {
            rng.Next((uint)secrets.Count);
            for (int i = 0; i < secrets.Count; i++)
            {
                int j = i + (int)rng.Uniform((uint)(secrets.Count - i));
                (secrets[i], secrets[j]) = (secrets[j], secrets[i]);
            }
        }
        else
        {
            var sorted = secrets.OrderBy(n => n.Tag, StringComparer.Ordinal).ToList();
            secrets.Clear();
            secrets.AddRange(sorted);
        }
    }

    /// <summary><c>0x142033660</c>: a LabyrinthSecrets row's room (+8) on the first collected node: its tag, exist 100,
    /// out of the collected list; a passage secret (+0x6d) also gives it an "entrance&lt;N&gt;" description (N: the labyrinth
    /// node's connections, then one more per passage); and the "secret1" / "secret2" description. None collected: the
    /// client logs "Not enough secret nodes in …" and goes on.</summary>
    void Give(List<GraphNode> secrets, int secret, string description, LabyrinthLayout.Node current, ref int entrances)
    {
        if (secret < 0) return;
        var table = Table("LabyrinthSecrets");
        string room = table.String(secret, 8);
        if (room.Length == 0 || secrets.Count == 0) return;
        var target = secrets[0];
        target.Tag = room;
        secrets.RemoveAt(0);
        target.Exist = 100;
        if (table.Bool(secret, 0x6d)) target.Descriptions.Add($"entrance{current.Links.Count + entrances++}");
        if (!target.Descriptions.Contains(description)) target.Descriptions.Add(description);
    }

    /// <summary>The gold door: the collected nodes are shuffled (all tagged "secret": <c>Next(count)</c> per place,
    /// front to back) or else sorted by tag (<c>0x1420393f0</c>, stable); the first takes the GoldDoor row's room name
    /// (+8) as its tag, exist 100, and a "secret1" description. None collected: the client logs "Not enough secret
    /// nodes in …" and goes on.</summary>
    void GoldDoor(List<GraphNode> secrets, bool plain, LevelRandom rng)
    {
        var table = Table("LabyrinthSecrets");
        int row = table.Find(r => table.String(r, 0) == "GoldDoor");
        if (row < 0) return;
        Order(secrets, plain, rng);
        string room = table.String(row, 8);
        if (room.Length == 0 || secrets.Count == 0) return;
        var door = secrets[0];
        door.Tag = room;
        // +0x6d (an "entrance<N>" description numbered from the labyrinth node) is clear in the GoldDoor row.
        if (table.Bool(row, 0x6d)) throw new NotSupportedException("LabyrinthSecrets +0x6d without a labyrinth node");
        secrets.RemoveAt(0);
        door.Exist = 100;
        if (!door.Descriptions.Contains("secret1")) door.Descriptions.Add("secret1");
    }

    RawDat Table(string name) => new(read($"Data/{name}.datc64") ?? throw new FileNotFoundException($"Data/{name}.datc64"));
}
