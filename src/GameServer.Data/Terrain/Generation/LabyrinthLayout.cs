namespace GameServer.Data.Terrain.Generation;

/// <summary>
/// The Labyrinth's day layout, <c>0x1424e30f0</c> (built by <c>0x1424e5ca0(…, seed, difficulty, node)</c>; the server
/// sends seed, node and difficulty in the area's extra data, <c>0x141ec7220</c> flag 0x08): the day's LabyrinthSection
/// per slot, their LabyrinthSectionLayout rows as nodes, the connections between them, and the secrets each node holds.
/// Checked against the client's own layouts with <c>scripts/re/frida/lab-layout.py</c> (<c>lg.ps1 lablayout</c>).
/// </summary>
public sealed class LabyrinthLayout
{
    /// <summary>A room of the layout (the client's 0x60-byte node): its LabyrinthSectionLayout row, its section and place in
    /// it (+0x10), its connections (node indexes; -1 is the way out: the plaza before the first, the boss after the
    /// last), up to two LabyrinthSecrets rows with their numbers, and, on a section's last room, the slot + 1.</summary>
    public sealed class Node(int row, int section, int place)
    {
        public int Row { get; } = row;
        public int Section { get; } = section;
        public int Place { get; } = place;
        public List<int> Links { get; } = [];
        public int Secret1 { get; internal set; } = -1;
        public int Secret2 { get; internal set; } = -1;
        public int Number1 { get; internal set; }
        public int Number2 { get; internal set; }
        public int SectionEnd { get; internal set; }
        internal bool HasFreeSecret => Secret1 < 0 || Secret2 < 0;
    }

    public uint Seed { get; }
    public int Difficulty { get; }
    public IReadOnlyList<Node> Nodes => _nodes;
    readonly List<Node> _nodes = [];
    /// <summary>The picked LabyrinthSection rows, one per slot.</summary>
    public IReadOnlyList<int> Sections => _sections;
    readonly List<int> _sections = [];

    // LabyrinthSectionLayout (0x6c-byte rows): section +0, place +0x10, connections +0x14, two secrets +0x24 / +0x34.
    const int LayoutSection = 0, LayoutPlace = 0x10, LayoutLinks = 0x14, LayoutSecret0 = 0x24, LayoutSecret1 = 0x34;
    // LabyrinthSecrets (0x96-byte rows): the secrets it brings along +0x10, weight +0x20, placement +0x24, number span
    // +0x58, a lieutenant-style end-room secret +0x6c, a passage it opens +0x6d, uses +0x6e, tiers +0x8d / +0x91, +0x95.
    const int SecretAdditional = 0x10, SecretWeight = 0x20, SecretPlacement = 0x24, SecretSpan = 0x58, SecretEndRoom = 0x6c,
        SecretPassage = 0x6d, SecretUses = 0x6e, SecretTierMin = 0x8d, SecretTierMax = 0x91, SecretSpread = 0x95;

    readonly RawDat _layouts, _secrets;
    readonly int[] _first, _last;
    // The client's draw counter (0x1424e53a0): 0x2e3, × 0x1f7 before every draw.
    uint _draws = 0x2e3;
    // The next secret number: 2 × nodes + 0x11, raised by each placed secret's span.
    int _number;
    readonly Dictionary<int, int> _used = [];

    LabyrinthLayout(Func<string, byte[]?> read, uint seed, int difficulty)
    {
        Seed = seed;
        Difficulty = difficulty;
        RawDat Table(string name) => new(read($"Data/{name}.datc64") ?? throw new FileNotFoundException($"Data/{name}.datc64"));
        var labyrinths = Table("Labyrinths");
        int lab = labyrinths.Find(r => labyrinths.I32(r, 0) == difficulty);
        if (lab < 0) throw new InvalidDataException("Invalid difficulty number!");
        _layouts = Table("LabyrinthSectionLayout");
        _secrets = Table("LabyrinthSecrets");
        PickSections(Table("LabyrinthSection"), labyrinths.I32(lab, 0), labyrinths.I32(lab, 0x40));
        _first = new int[_sections.Count];
        _last = new int[_sections.Count];
        BuildNodes();
        Connect();
        PlaceSecrets(labyrinths.I32List(lab, 0x64), labyrinths.I32List(lab, 0x54));
    }

    /// <summary>The layout the client builds for this seed and difficulty (1 Normal … 4 Eternal).</summary>
    public static LabyrinthLayout Build(Func<string, byte[]?> read, uint seed, int difficulty) => new(read, seed, difficulty);

    /// <summary>The official server's pick of a node's area among the <paramref name="count"/> of its LabyrinthAreas row
    /// for the tier: a TinyMT seeded from (difficulty + 4 × the node's LabyrinthSectionLayout row) × 0x13d + seed, as the
    /// client's boss override pick (<see cref="LabyrinthPass"/>). Not in the client; it matched every room with a choice
    /// in poelab.com's layouts of six days (the day's seed being the day's number since 1970).</summary>
    public int AreaPick(Node node, int count) =>
        (int)new TinyMt((uint)((Difficulty + 4 * node.Row) * 0x13d + Seed)).Pick((uint)count);

    /// <summary><c>0x1424e2c10</c>: one LabyrinthSection per slot. From row 1 on, the rows whose slot range (+0x20, +0x24)
    /// holds slot + 1 and whose tier range (+0x18, +0x1c) holds the tier, without one already picked (no exclusion
    /// group, +8) or one of a picked row's group; a TinyMT seeded from seed + 0x11 + (tier + 4 × slot) × 0x13d picks.</summary>
    void PickSections(RawDat sections, int tier, int slots)
    {
        for (int slot = 0; slot < slots; slot++)
        {
            var candidates = new List<int>();
            for (int r = 1; r < sections.Count; r++)
            {
                if (sections.I32(r, 0x20) > slot + 1 || slot + 1 > sections.I32(r, 0x24)) continue;
                if (sections.I32(r, 0x18) > tier || tier > sections.I32(r, 0x1c)) continue;
                int group = sections.Ref(r, 8);
                if (group < 0 ? _sections.Contains(r) : _sections.Any(p => sections.Ref(p, 8) == group)) continue;
                candidates.Add(r);
            }
            if (candidates.Count == 0) throw new InvalidDataException("No LabyrinthSections could be used!");
            _sections.Add(candidates[(int)new TinyMt((uint)(Seed + 0x11 + (tier + slot * 4) * 0x13d)).Pick((uint)candidates.Count)]);
        }
    }

    /// <summary>Each section's layout rows become nodes, ordered by their place (+0x10, <c>0x1424e62c0</c>); a section's
    /// last node carries slot + 1 (+0x5a).</summary>
    void BuildNodes()
    {
        for (int slot = 0; slot < _sections.Count; slot++)
        {
            int section = _sections[slot];
            var rows = Enumerable.Range(0, _layouts.Count).Where(r => _layouts.Ref(r, LayoutSection) == section)
                .OrderBy(r => _layouts.I32(r, LayoutPlace)).ToList();
            if (rows.Count == 0) throw new InvalidDataException($"LabyrinthSection {section} has no layout rows");
            _first[slot] = _nodes.Count;
            foreach (int r in rows) _nodes.Add(new Node(r, section, _layouts.I32(r, LayoutPlace)));
            _last[slot] = _nodes.Count - 1;
            _nodes[^1].SectionEnd = slot + 1;
        }
    }

    /// <summary>The connections, in the client's order: the way in on the first node; each section's first node to the
    /// previous section's last; every node to the rows its layout row lists (a row at place 0 is the link between
    /// sections, made separately); each section's last node to the next section's first; the way out on the last.
    /// A node has at most five ("Too many connections to LabyrinthNode").</summary>
    void Connect()
    {
        Link(0, -1);
        for (int slot = 1; slot < _sections.Count; slot++) Link(_first[slot], _last[slot - 1]);
        for (int i = 0; i < _nodes.Count; i++)
        {
            int row = _nodes[i].Row, place = _layouts.I32(row, LayoutPlace);
            foreach (int other in _layouts.RefList8(row, LayoutLinks))
                if (_layouts.I32(other, LayoutPlace) is int otherPlace and not 0) Link(i, otherPlace - place + i);
        }
        for (int slot = 0; slot + 1 < _sections.Count; slot++) Link(_last[slot], _first[slot + 1]);
        Link(_nodes.Count - 1, -1);
    }

    void Link(int from, int to)
    {
        if (_nodes[from].Links.Count >= 5) throw new InvalidDataException("Too many connections to LabyrinthNode");
        _nodes[from].Links.Add(to);
    }

    /// <summary>One draw of the client's layout generator (<c>0x1424e53a0</c>): the counter × 0x1f7, then a TinyMT seeded
    /// from (difficulty + 4 × counter) × 0x13d + seed picks below n (no draw for fewer than two).</summary>
    int Draw(int n)
    {
        _draws *= 0x1f7;
        return n < 2 ? 0 : (int)new TinyMt((uint)((Difficulty + _draws * 4) * 0x13d + Seed)).Pick((uint)n);
    }

    /// <summary>A weighted bag of LabyrinthSecrets rows (the client's (row, weight) vectors with their total).</summary>
    sealed class Bag
    {
        public readonly List<(int Secret, int Weight)> Entries = [];
        public int Total;
        public void Add(int secret, int weight) { Entries.Add((secret, weight)); Total += weight; }
        public Bag Copy() { var b = new Bag(); foreach (var e in Entries) b.Add(e.Secret, e.Weight); return b; }
    }

    /// <summary><c>0x1424e5420</c>: two draws below the total (the first is thrown away), the entry whose weight span holds
    /// the second, taken out of the bag.</summary>
    int Take(Bag bag)
    {
        Draw(bag.Total);
        int r = Draw(bag.Total), i = 0;
        while (i < bag.Entries.Count - 1 && r >= bag.Entries[i].Weight) { r -= bag.Entries[i].Weight; i++; }
        var (secret, weight) = bag.Entries[i];
        bag.Entries.RemoveAt(i);
        bag.Total -= weight;
        return secret;
    }

    /// <summary>A node's free secret place (the first, else the second) takes the secret and a number.</summary>
    void Assign(int node, int secret, int number)
    {
        var n = _nodes[node];
        if (n.Secret1 < 0) { n.Secret1 = secret; n.Number1 = number; }
        else if (n.Secret2 < 0) { n.Secret2 = secret; n.Number2 = number; }
    }

    /// <summary><c>0x1424e55f0</c>: a node in [from, to) with a free secret place, not already holding the secret first
    /// and not <paramref name="exclude"/>, drawn among them; -1 when none.</summary>
    int PickNode(int from, int to, int secret, int exclude)
    {
        var candidates = new List<int>();
        for (int i = from; i < to; i++)
            if (_nodes[i].HasFreeSecret && _nodes[i].Secret1 != secret && i != exclude) candidates.Add(i);
        return candidates.Count == 0 ? -1 : candidates[Draw(candidates.Count)];
    }

    int Uses(int secret) => _used.GetValueOrDefault(secret);

    /// <summary><c>0x1424e57f0</c>: a secret that brings others along (a key and its door, the statues' two halves): its
    /// node in the section (one before the last for placement 1), then each companion where the placement says (1: later
    /// in the section; 2: the next section; 3: the section's last node, when its second place is free; 4: the last
    /// section; 5: a later dead end), else (not 1 or 3) any later node. The main secret then takes its node; both get
    /// the current number, and a passage secret (+0x6d) connects the two nodes. False when no node fits.</summary>
    bool PlaceLinked(int slot, int secret)
    {
        int placement = _secrets.I32(secret, SecretPlacement);
        int node = PickNode(_first[slot], _last[slot] - (placement == 1 ? 1 : 0), secret, -1);
        if (node < 0) return false;
        int lastSlot = _sections.Count - 1;
        foreach (int companion in _secrets.RefList8(secret, SecretAdditional))
        {
            int at = placement switch
            {
                1 => PickNode(node + 1, _last[slot], companion, node),
                2 => PickNode(_first[slot + 1], _last[slot + 1], companion, node),
                3 => _nodes[_last[slot]].Secret2 < 0 ? _last[slot] : -2,
                4 => PickNode(_first[lastSlot], _last[lastSlot], companion, node),
                5 => DeadEnd(node, companion),
                _ => -1,
            };
            if (at == -2) return false;
            if (at < 0)
            {
                if (placement is 1 or 3) return false;
                at = PickNode(node + 1, _last[lastSlot], companion, node);
                if (at < 0) return false;
            }
            Assign(node, secret, _number);
            Assign(at, companion, _number);
            if (_secrets.Bool(secret, SecretPassage))
            {
                Link(node, at);
                Link(at, node);
            }
        }
        return true;
    }

    // Placement 5: a later node up to the last section's end with a free place, one connection (a dead end), not holding
    // the companion first; drawn among them.
    int DeadEnd(int node, int companion)
    {
        var candidates = new List<int>();
        for (int i = node + 1; i < _last[^1]; i++)
            if (_nodes[i].HasFreeSecret && _nodes[i].Secret1 != companion && _nodes[i].Links.Count == 1) candidates.Add(i);
        return candidates.Count == 0 ? -1 : candidates[Draw(candidates.Count)];
    }

    /// <summary>The secrets, in the client's passes: the layout rows' own (+0x24, +0x34); one end-room secret (+0x6c) on
    /// each section's last node but the final one, whose companion goes on every later section's last node with the
    /// same number; two spread secrets (+0x95) anywhere but the first section's last node; then per slot a number of
    /// the day's random secrets between its Labyrinths minimum (+0x64) and maximum (+0x54). A secret is a candidate for
    /// the tier (+0x8d..+0x91) with a weight; numbers stop below 0x61.</summary>
    void PlaceSecrets(int[] minimum, int[] maximum)
    {
        _number = _nodes.Count * 2 + 0x11;
        var random = new Bag();
        var endRoom = new Bag();
        var spread = new Bag();
        for (int s = 0; s < _secrets.Count; s++)
        {
            int weight = _secrets.I32(s, SecretWeight);
            if (weight == 0 || Difficulty < _secrets.I32(s, SecretTierMin) || _secrets.I32(s, SecretTierMax) < Difficulty) continue;
            if (_secrets.Bool(s, SecretEndRoom)) { endRoom.Add(s, weight); continue; }
            random.Add(s, weight);
            if (_secrets.Bool(s, SecretSpread) && _secrets.RefList8(s, SecretAdditional).Length == 0 && _secrets.I32(s, SecretSpan) == 1)
                spread.Add(s, weight);
        }

        // The layout rows' own secrets.
        for (int slot = 0; slot < _sections.Count; slot++)
            for (int i = _first[slot]; i <= _last[slot]; i++)
                foreach (int at in new[] { LayoutSecret0, LayoutSecret1 })
                {
                    int secret = _layouts.Ref(_nodes[i].Row, at);
                    if (secret < 0) continue;
                    _used[secret] = Uses(secret) + 1;
                    if (_secrets.RefList8(secret, SecretAdditional).Length == 0) Assign(i, secret, _number);
                    else PlaceLinked(slot, secret);
                    _number += _secrets.I32(secret, SecretSpan);
                }

        // An end-room secret on each section's last node but the final one.
        for (int slot = 0; slot + 1 < _sections.Count; slot++)
        {
            if (endRoom.Entries.Count == 0) continue;
            int secret = Take(endRoom);
            Assign(_last[slot], secret, _number);
            _number += _secrets.I32(secret, SecretSpan);
        }
        // Their companions on every later section's last node, with the same number (m_AdditionalSecrets).
        for (int slot = 0; slot + 1 < _sections.Count; slot++)
        {
            var end = _nodes[_last[slot]];
            if (end.Secret1 < 0 || _secrets.RefList8(end.Secret1, SecretAdditional) is not [int companion, ..]) continue;
            for (int later = slot + 1; later < _sections.Count; later++) Assign(_last[later], companion, end.Number1);
        }

        // Two spread secrets anywhere but the first section's last node.
        for (int pass = 0; pass < 2; pass++)
            while (spread.Entries.Count > 0)
            {
                int secret = Take(spread);
                int span = _secrets.I32(secret, SecretSpan);
                if (span + _number >= 0x61 || Uses(secret) >= _secrets.I32(secret, SecretUses)) continue;
                int node = PickNode(0, _nodes.Count, secret, _last[0]);
                if (node < 0) continue;
                Assign(node, secret, _number);
                if (_secrets.I32(secret, SecretUses) > 1) spread.Add(secret, _secrets.I32(secret, SecretWeight));
                _number += span;
                _used[secret] = Uses(secret) + 1;
                break;
            }

        // The day's random secrets, per slot.
        for (int slot = 0; slot < _sections.Count; slot++)
        {
            int count = minimum[slot] + Draw(maximum[slot] - minimum[slot] + 1);
            var bag = random.Copy();
            for (int k = 0; k < count; k++)
                while (bag.Entries.Count > 0)
                {
                    int secret = Take(bag);
                    int span = _secrets.I32(secret, SecretSpan);
                    if (span + _number >= 0x61) continue;
                    int placement = _secrets.I32(secret, SecretPlacement);
                    if (placement == 2 ? slot + 1 >= _sections.Count : placement == 3 && _nodes[_last[slot]].Secret2 >= 0) continue;
                    if (Uses(secret) >= _secrets.I32(secret, SecretUses)) continue;
                    if (_secrets.RefList8(secret, SecretAdditional).Length == 0)
                    {
                        int node = PickNode(_first[slot], _last[slot], secret, -1);
                        if (node < 0) continue;
                        Assign(node, secret, _number);
                    }
                    else if (!PlaceLinked(slot, secret)) continue;
                    if (_secrets.I32(secret, SecretUses) > 1) bag.Add(secret, _secrets.I32(secret, SecretWeight));
                    _number += span;
                    _used[secret] = Uses(secret) + 1;
                    break;
                }
        }
    }
}
