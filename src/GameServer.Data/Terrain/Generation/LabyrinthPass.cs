namespace GameServer.Data.Terrain.Generation;

/// <summary><c>0x142032760</c> (.dgr, from <c>0x14203c900</c>) / <c>0x142033e30</c> (.tgr, from <c>0x14203f890</c>): the
/// Labyrinth's graph pass, for an area whose WorldAreas row has +0x171 set (<see cref="AreaInfo.IsLabyrinth"/>), after
/// the tag choices and side areas and before the description pass (<c>0x142034e60</c>). Ported for an instance without
/// a labyrinth node (instance +0x48, the server's daily layout: its LabyrinthNodeOverrides and the secrets it assigns,
/// <c>0x142033660</c>, are not ported); that is what the Gateway sends.</summary>
sealed class LabyrinthPass(Func<string, byte[]?> read, string areaId, bool boss)
{
    /// <summary>Runs the pass over the graph's nodes. Every node tagged "secret" or "secret" plus one character
    /// (<c>0x14203a2e0</c>) is collected and loses its exist chance (+0x44 = 0). A boss area (+0x1bb) applies one
    /// LabyrinthNodeOverrides row; any other area named "_quad_door" or "_branch_door" turns the first collected node
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
        if (boss)
        {
            // 0x1424e5ca0 builds the area's labyrinth (difficulty: the id's first character, 'E' = 4) for the pick
            // below; "_end" areas take the final boss row (Normal: the one without traps), the others Boss1_Sec1.
            int difficulty = areaId[0] == 'E' ? 4 : areaId[0] - '0';
            string row = areaId.Contains("_end", StringComparison.Ordinal) ? difficulty == 1 ? "FinalBossEasy" : "FinalBoss" : "Boss1_Sec1";
            Override(graph, row, difficulty);
        }
        else if (areaId.Contains("_quad_door", StringComparison.Ordinal) || areaId.Contains("_branch_door", StringComparison.Ordinal))
            GoldDoor(secrets, plain, rng);
        foreach (var n in secrets)
        {
            n.Exist = 0;
            n.Descriptions.Clear();
        }
    }

    /// <summary><c>0x1420339a0</c> → <c>0x142033ad0</c>: every node whose tag is the row's node name (+8) takes one of
    /// its overrides (+0x10): none empties the tag, one is taken as it is; with several, <c>0x1424e5ee0</c> picks with
    /// a TinyMT seeded from (difficulty + 4 × the layout node's LabyrinthSectionLayout row) × 0x13d + the layout's seed
    /// (no level draw). The node is the layout's last (<see cref="LastLayoutRow"/>).</summary>
    void Override(TileGraph graph, string id, int difficulty)
    {
        var table = Table("LabyrinthNodeOverrides");
        int row = table.Find(r => table.String(r, 0) == id);
        if (row < 0) return;
        string name = table.String(row, 8);
        if (name.Length == 0) return;
        var overrides = table.StringList(row, 0x10);
        foreach (var n in graph.Nodes)
        {
            if (n.Tag != name) continue;
            int pick = overrides.Length < 2 ? 0
                : (int)new TinyMt((uint)((difficulty + 4 * LastLayoutRow(difficulty)) * 0x13d + LayoutSeed)).Pick((uint)overrides.Length);
            n.Tag = overrides.Length == 0 ? "" : overrides[pick];
        }
    }

    /// <summary>The labyrinth layout's seed: the boss pass builds it with 0 (<c>0x1424e5ca0(…, 0, difficulty, -1)</c>);
    /// the server's daily layout (read by <c>0x141ec7220</c>: u32 seed, u16 node, u8 difficulty) is not sent.</summary>
    const uint LayoutSeed = 0;

    /// <summary><c>0x1424e30f0</c>, as far as the boss pick needs it: the LabyrinthSectionLayout row of the layout's last
    /// node. <c>0x1424e2c10</c> picks one LabyrinthSection per slot (Labyrinths +0x40 slots, the row whose tier +0 is
    /// the difficulty): from row 1 on, the rows whose slot range (+0x20, +0x24) holds slot + 1 and whose tier range
    /// (+0x18, +0x1c) holds the tier, without one already picked (no exclusion group, +8) or one of a picked row's group;
    /// a TinyMT seeded from seed + 0x11 + (tier + 4 × slot) × 0x13d picks among them. Each section's layout rows
    /// (LabyrinthSectionLayout +0) become nodes, sorted by +0x10 (<c>0x1424e62c0</c>; insertion sort, stable, for
    /// so few).</summary>
    int LastLayoutRow(int difficulty)
    {
        var labyrinths = Table("Labyrinths");
        int lab = labyrinths.Find(r => labyrinths.I32(r, 0) == difficulty);
        if (lab < 0) throw new InvalidDataException("Invalid difficulty number!");
        int tier = labyrinths.I32(lab, 0), slots = labyrinths.I32(lab, 0x40);
        var sections = Table("LabyrinthSection");
        var picked = new List<int>();
        for (int slot = 0; slot < slots; slot++)
        {
            var candidates = new List<int>();
            for (int r = 1; r < sections.Count; r++)
            {
                if (sections.I32(r, 0x20) > slot + 1 || slot + 1 > sections.I32(r, 0x24)) continue;
                if (sections.I32(r, 0x18) > tier || tier > sections.I32(r, 0x1c)) continue;
                int group = sections.Ref(r, 8);
                if (group < 0 ? picked.Contains(r) : picked.Any(p => sections.Ref(p, 8) == group)) continue;
                candidates.Add(r);
            }
            if (candidates.Count == 0) throw new InvalidDataException("No LabyrinthSections could be used!");
            picked.Add(candidates[(int)new TinyMt((uint)(LayoutSeed + 0x11 + (tier + slot * 4) * 0x13d)).Pick((uint)candidates.Count)]);
        }
        var layouts = Table("LabyrinthSectionLayout");
        int last = picked[^1];
        var rows = Enumerable.Range(0, layouts.Count).Where(r => layouts.Ref(r, 0) == last).OrderBy(r => layouts.I32(r, 0x10)).ToList();
        if (rows.Count == 0) throw new InvalidDataException($"LabyrinthSection {last} has no layout rows");
        return rows[^1];
    }

    /// <summary>The client's small generator (TinyMT32's parameters): <c>0x140e0bec0</c> seeds it (TinyMT32's
    /// initialisation, then eight steps) and <c>0x140484fa0</c> draws a uniform index (32-bit outputs, the last
    /// incomplete bucket rejected). Its step keeps the client's order, which differs from TinyMT32's in which word
    /// gets which shift.</summary>
    struct TinyMt
    {
        const uint Mat1 = 0x8f7011ee, Mat2 = 0xfc78ff1f, TMat = 0x3793fdff;
        uint _s0, _s1, _s2, _s3;

        public TinyMt(uint seed)
        {
            uint[] s = [seed, Mat1, Mat2, TMat];
            for (uint i = 1; i < 8; i++)
                s[i & 3] ^= i + 0x6c078965u * (s[(i - 1) & 3] ^ (s[(i - 1) & 3] >> 30));
            (_s0, _s1, _s2, _s3) = (s[0], s[1], s[2], s[3]);
            for (int i = 0; i < 8; i++) Next();
        }

        uint Next()
        {
            uint x = (_s0 & 0x7fffffff) ^ _s1 ^ _s2;
            uint y = _s3 ^ (_s3 << 1);
            x = (x >> 1) ^ x ^ y;
            uint s1 = _s1;
            _s0 = s1;
            _s1 = (0u - (x & 1) & Mat1) ^ _s2;
            _s2 = (0u - (x & 1) & Mat2) ^ (x << 10) ^ y;
            _s3 = x;
            uint t = (_s2 >> 8) + s1;
            return (0u - (t & 1) & TMat) ^ t ^ x;
        }

        public uint Pick(uint n)
        {
            if (n <= 1) return 0;
            uint v;
            do v = Next();
            while (uint.MaxValue / n <= v / n && uint.MaxValue % n != n - 1);
            return v % n;
        }
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
        if (plain)
            for (int i = 0; i < secrets.Count; i++)
            {
                int j = i + (int)rng.Next((uint)(secrets.Count - i));
                (secrets[i], secrets[j]) = (secrets[j], secrets[i]);
            }
        else
        {
            var sorted = secrets.OrderBy(n => n.Tag, StringComparer.Ordinal).ToList();
            secrets.Clear();
            secrets.AddRange(sorted);
        }
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
