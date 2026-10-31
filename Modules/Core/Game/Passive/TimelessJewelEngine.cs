using System.Text.Json;


/// <summary>
/// The client's alternate-passive generator for timeless jewels. The rules and the TinyMT32 RNG are the ones reverse
/// engineered by Vilsol/timeless-jewels and used to build Path of Building's LUTs; the data comes from the target
/// client's AlternateTreeVersions, AlternatePassiveSkills and AlternatePassiveAdditions tables.
/// </summary>
sealed class TimelessJewelEngine(string folder = "data")
{
    public sealed record AlternateNode(bool Replaces, IReadOnlyList<(uint Key, int Value)> Stats, string Name);

    sealed record TreeVersion(int Key, bool ReplaceSmallAttribute, bool ReplaceSmallNormal,
        int MinAddSmallAttribute, int MaxAddSmallAttribute, int MinAddSmallNormal, int MaxAddSmallNormal,
        int MinAddNotable, int MaxAddNotable, int NotableWeight);

    sealed record AltSkill(int Row, string Id, string Name, int TreeVersion, int[] PassiveTypes, int[] StatsKeys,
        int[] Min, int[] Max, int SpawnWeight, int ConquerorIndex, int ConquerorVersion, int RandomMin, int RandomMax);

    sealed record AltAddition(int Row, string Id, int TreeVersion, int[] PassiveTypes, int[] StatsKeys,
        int[] Min, int[] Max, int SpawnWeight);

    sealed record Data(
        IReadOnlyDictionary<int, TreeVersion> Versions,
        IReadOnlyDictionary<(int Version, int Type), AltSkill[]> Skills,
        IReadOnlyDictionary<(int Version, int Type), AltAddition[]> Additions,
        IReadOnlyDictionary<(int Version, int Conqueror, int ConquerorVersion), AltSkill> Keystones);

    readonly Lazy<Data?> _data = new(() => Load(folder));

    public bool Loaded => _data.Value is not null;

    /// <summary>The alternate version of a node, or null when the node is not a passive a timeless jewel may conquer.
    /// <see cref="AlternateNode.Replaces"/> says whether the original stats are removed (replacement) or kept and the
    /// returned stats are additions.</summary>
    public AlternateNode? Alternate(PassiveTree.Node node, TimelessSpec spec, uint graphId)
    {
        if (_data.Value is not { } data || !Eligible(node)) return null;
        int type = Type(node);
        if (!data.Versions.TryGetValue(spec.TreeVersion, out var version)) return null;

        if (type == 4)
        {
            if (!data.Keystones.TryGetValue((spec.TreeVersion, spec.ConquerorIndex, spec.ConquerorVersion), out var stone)
                && !data.Keystones.TryGetValue((spec.TreeVersion, spec.ConquerorIndex, 0), out stone)) return null;
            if (stone.StatsKeys.Length == 0 || stone.Min.Length == 0) return null;
            return new(true, [((uint)stone.StatsKeys[0] + 1, stone.Min[0])], stone.Name);
        }

        bool replace = type switch
        {
            3 => version.NotableWeight >= 100
                 || version.NotableWeight > 0 && Roll(graphId, spec.Seed, 0, 100) < (uint)version.NotableWeight,
            1 => version.ReplaceSmallAttribute,
            _ => version.ReplaceSmallNormal,
        };
        return replace
            ? Replace(data, node, spec, graphId, version, type)
            : Augment(data, node, spec, graphId, version, type);
    }

    static bool Eligible(PassiveTree.Node node) =>
        !node.IsJewelSocket && !node.IsAscendancy && !node.IsMastery && !node.IsProxy && !node.IsExpansion
        && !node.IsStart && node.Stats.Count > 0;

    // The client's classification: one base Strength/Dexterity/Intelligence stat is the small-attribute type. Its stat
    // keys are 576, 579 and 582 (the rows are 0-based in PassiveSkills/Mods, client keys are row + 1); the client checks
    // them as (key - 576) in mask 0x49.
    static int Type(PassiveTree.Node node) =>
        node.IsKeystone ? 4
        : node.IsNotable ? 3
        : node.Stats.Count == 1 && node.Stats[0].Key is 576 or 579 or 582 ? 1
        : 2;

    static AlternateNode? Replace(Data data, PassiveTree.Node node, TimelessSpec spec, uint graphId,
        TreeVersion version, int type)
    {
        if (!data.Skills.TryGetValue((spec.TreeVersion, type), out var candidates) || candidates.Length == 0)
            return null;
        var rng = new TinyMt32(graphId, (uint)spec.Seed);
        if (type == 3) rng.Generate(0, 100);   // notable pre-roll, before the weighted choice
        if (Pick(rng, candidates) is not { } chosen) return null;
        var stats = Stats(rng, chosen.StatsKeys, chosen.Min, chosen.Max, 4);
        var (min, max) = Additions(version, type);
        stats.AddRange(RollAdditions(rng, data, spec.TreeVersion, type, min + chosen.RandomMin, max + chosen.RandomMax));
        return new(true, stats, chosen.Name);
    }

    static AlternateNode? Augment(Data data, PassiveTree.Node node, TimelessSpec spec, uint graphId,
        TreeVersion version, int type)
    {
        var rng = new TinyMt32(graphId, (uint)spec.Seed);
        if (type == 3) rng.Generate(0, 100);   // notable pre-roll
        var (min, max) = Additions(version, type);
        return new(false, RollAdditions(rng, data, spec.TreeVersion, type, min, max), "");
    }

    static (int Min, int Max) Additions(TreeVersion version, int type) => type switch
    {
        1 => (version.MinAddSmallAttribute, version.MaxAddSmallAttribute),
        3 => (version.MinAddNotable, version.MaxAddNotable),
        _ => (version.MinAddSmallNormal, version.MaxAddSmallNormal),
    };

    static AltSkill? Pick(TinyMt32 rng, AltSkill[] candidates)
    {
        AltSkill? picked = null;
        uint cumulative = 0;
        foreach (var candidate in candidates)
        {
            cumulative += (uint)candidate.SpawnWeight;
            if (cumulative > 0 && rng.GenerateSingle(cumulative) < (uint)candidate.SpawnWeight) picked = candidate;
        }
        return picked;
    }

    static List<(uint Key, int Value)> RollAdditions(TinyMt32 rng, Data data, int treeVersion, int type,
        int minimum, int maximum)
    {
        int count = maximum > minimum ? (int)rng.Generate((uint)minimum, (uint)maximum) : minimum;
        var result = new List<(uint, int)>();
        for (int i = 0; i < count; i++)
        {
            AltAddition? addition = null;
            // The client retries until it rolls one; a table with no positive weights would otherwise spin forever.
            for (int attempt = 0; attempt < 64 && addition is null; attempt++)
                addition = PickAddition(rng, data, treeVersion, type);
            if (addition is null) break;
            result.AddRange(Stats(rng, addition.StatsKeys, addition.Min, addition.Max, 2));
        }
        return result;
    }

    static AltAddition? PickAddition(TinyMt32 rng, Data data, int treeVersion, int type)
    {
        if (!data.Additions.TryGetValue((treeVersion, type), out var candidates) || candidates.Length == 0)
            return null;
        uint total = 0;
        foreach (var candidate in candidates) total += (uint)candidate.SpawnWeight;
        if (total == 0) return null;
        uint roll = rng.GenerateSingle(total);
        foreach (var candidate in candidates)
        {
            if ((uint)candidate.SpawnWeight > roll) return candidate;
            roll -= (uint)candidate.SpawnWeight;
        }
        return null;
    }

    static List<(uint Key, int Value)> Stats(TinyMt32 rng, int[] keys, int[] min, int[] max, int limit)
    {
        var result = new List<(uint, int)>();
        int count = Math.Min(Math.Min(keys.Length, min.Length), Math.Min(max.Length, limit));
        for (int i = 0; i < count; i++)
            result.Add(((uint)keys[i] + 1, max[i] > min[i] ? (int)rng.Generate((uint)min[i], (uint)max[i]) : min[i]));
        return result;
    }

    static uint Roll(uint graphId, int seed, uint min, uint max) =>
        new TinyMt32(graphId, (uint)seed).Generate(min, max);

    static Data? Load(string folder)
    {
        string versionsPath = Path.Combine(folder, "tables", "AlternateTreeVersions.json");
        string skillsPath = Path.Combine(folder, "tables", "AlternatePassiveSkills.json");
        string additionsPath = Path.Combine(folder, "tables", "AlternatePassiveAdditions.json");
        if (!new[] { versionsPath, skillsPath, additionsPath }.All(File.Exists)) return null;

        var versions = new Dictionary<int, TreeVersion>();
        using (var d = GameTables.Open(versionsPath))
        {
            int row = 0;
            foreach (var r in d.RootElement.EnumerateArray())
            {
                int key = row++;
                if (key == 0) continue;
                versions[key] = new(key, Bool(r, "__col1"), Bool(r, "__col2"),
                    Int(r, "__col3"), Int(r, "__col4"), Int(r, "__col5"), Int(r, "__col6"),
                    Int(r, "__col7"), Int(r, "__col8"), Int(r, "__col9"));
            }
        }

        var skills = new Dictionary<(int, int), List<AltSkill>>();
        var keystones = new Dictionary<(int, int, int), AltSkill>();
        using (var d = JsonDocument.Parse(File.ReadAllBytes(skillsPath)))
        {
            int row = 0;
            foreach (var r in d.RootElement.EnumerateArray())
            {
                var types = Ints(r, "PassiveType");
                var skill = new AltSkill(row++, Str(r, "Id"), Str(r, "Name"), Int(r, "AlternateTreeVersionsKey"),
                    types, Ints(r, "StatsKeys"),
                    [Int(r, "Stat1Min"), Int(r, "Stat2Min")], [Int(r, "Stat1Max"), Int(r, "Stat2Max")],
                    Int(r, "SpawnWeight"), Int(r, "__col18"), Int(r, "__col24"), Int(r, "RandomMin"), Int(r, "RandomMax"));
                foreach (int type in types)
                {
                    if (!skills.TryGetValue((skill.TreeVersion, type), out var list))
                        skills[(skill.TreeVersion, type)] = list = [];
                    list.Add(skill);
                }
                if (types.Contains(4))
                    keystones.TryAdd((skill.TreeVersion, skill.ConquerorIndex, skill.ConquerorVersion), skill);
            }
        }

        var additions = new Dictionary<(int, int), List<AltAddition>>();
        using (var d = JsonDocument.Parse(File.ReadAllBytes(additionsPath)))
        {
            int row = 0;
            foreach (var r in d.RootElement.EnumerateArray())
            {
                var addition = new AltAddition(row++, Str(r, "Id"), Int(r, "AlternateTreeVersionsKey"),
                    Ints(r, "PassiveType"), Ints(r, "StatsKeys"),
                    [Int(r, "Stat1Min"), Int(r, "__col6")], [Int(r, "Stat1Max"), Int(r, "__col7")],
                    Int(r, "SpawnWeight"));
                foreach (int type in addition.PassiveTypes)
                {
                    if (!additions.TryGetValue((addition.TreeVersion, type), out var list))
                        additions[(addition.TreeVersion, type)] = list = [];
                    list.Add(addition);
                }
            }
        }

        return new(versions,
            skills.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            additions.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            keystones);
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static int[] Ints(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32())]
            : [];
}

/// <summary>The client's TinyMT32 generator, ported from Vilsol/timeless-jewels' random/main.go. Legion jewels use the
/// plain modulo draw (not the Abyss MSVC rejection draw).</summary>
sealed class TinyMt32
{
    const uint InitialStateConstant0 = 0x40336050, InitialStateConstant1 = 0xCFA3723C,
        InitialStateConstant2 = 0x3CAC5F6F, InitialStateConstant3 = 0x3793FDFF;
    const uint TinyMT32SH0 = 1, TinyMT32SH1 = 10, TinyMT32Mask = 0x7FFFFFFF,
        TinyMT32Alpha = 0x19660D, TinyMT32Bravo = 0x5D588B65;

    readonly uint[] state = new uint[4];

    public TinyMt32(uint graphId, uint seed)
    {
        state[0] = InitialStateConstant0;
        state[1] = InitialStateConstant1;
        state[2] = InitialStateConstant2;
        state[3] = InitialStateConstant3;
        Initialize(graphId, seed);
    }

    void Initialize(uint graphId, uint seed)
    {
        uint index = 1;
        for (int i = 0; i < 2; i++)
        {
            uint round = ManipulateAlpha(state[index % 4] ^ state[(index + 1) % 4] ^ state[(index + 3) % 4]);
            state[(index + 1) % 4] += round;
            round += (i == 0 ? graphId : seed) + index;
            state[(index + 2) % 4] += round;
            state[index % 4] = round;
            index = (index + 1) % 4;
        }
        for (int i = 0; i < 5; i++)
        {
            uint round = ManipulateAlpha(state[index % 4] ^ state[(index + 1) % 4] ^ state[(index + 3) % 4]);
            state[(index + 1) % 4] += round;
            round += index;
            state[(index + 2) % 4] += round;
            state[index % 4] = round;
            index = (index + 1) % 4;
        }
        for (int i = 0; i < 4; i++)
        {
            uint round = ManipulateBravo(state[index % 4] + state[(index + 1) % 4] + state[(index + 3) % 4]);
            state[(index + 1) % 4] ^= round;
            round -= index;
            state[(index + 2) % 4] ^= round;
            state[index % 4] = round;
            index = (index + 1) % 4;
        }
        for (int i = 0; i < 8; i++) GenerateNextState();
    }

    public uint Generate(uint min, uint max)
    {
        uint a = unchecked(min + 0x80000000), b = unchecked(max + 0x80000000);
        uint roll = GenerateSingle(unchecked(b - a + 1));
        return unchecked(roll + a + 0x80000000);
    }

    public uint GenerateSingle(uint exclusiveMaximum)
    {
        if (exclusiveMaximum == 0) return 0;
        return GenerateUInt() % exclusiveMaximum;
    }

    uint GenerateUInt()
    {
        GenerateNextState();
        return Temper();
    }

    void GenerateNextState()
    {
        uint a = state[3];
        uint b = ((state[0] & TinyMT32Mask) ^ state[1]) ^ state[2];
        a ^= a << (int)TinyMT32SH0;
        b ^= (b >> (int)TinyMT32SH0) ^ a;
        state[0] = state[1];
        state[1] = state[2];
        state[2] = a ^ (b << (int)TinyMT32SH1);
        state[3] = b;
        state[1] ^= unchecked((uint)-(int)(b & 1)) & 0x8F7011EE;
        state[2] ^= unchecked((uint)-(int)(b & 1)) & 0xFC78FF1F;
    }

    uint Temper()
    {
        uint b = state[0] + (state[2] >> 8);
        uint a = state[3] ^ b;
        return a ^ (unchecked((uint)-(int)(b & 1)) & 0x3793FDFF);
    }

    static uint ManipulateAlpha(uint value) => unchecked((value ^ (value >> 27)) * TinyMT32Alpha);
    static uint ManipulateBravo(uint value) => unchecked((value ^ (value >> 27)) * TinyMT32Bravo);
}
