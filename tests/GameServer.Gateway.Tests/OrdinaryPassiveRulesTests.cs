using System.Text.Json;

namespace GameServer.Gateway.Tests;

public sealed class OrdinaryPassiveRulesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "localexile-ordinary-passives-" + Guid.NewGuid().ToString("N"));

    public OrdinaryPassiveRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "tables"));
        Write("tables/Characters.json", new[] { new { IntegerId = 3 }, new { IntegerId = 0 }, new { IntegerId = 1 } });
        Write("tables/Stats.json", new[] { new { Id = "display_can_take_character_start_point" } });
        object Passive(int id, int[]? characters = null, bool ascendancy = false, int? grant = null) => new
        {
            PassiveSkillGraphId = id,
            Characters = characters ?? [],
            AscendancyKey = ascendancy ? (int?)0 : null,
            Stats = grant is null ? Array.Empty<int>() : [0],
            Stat1Value = grant ?? 0,
            MasteryGroup = id == 14 ? 0 : -1,
            IsProxyPassive = id == 97,
            IsExpansion = id == 98,
        };
        Write("tables/PassiveSkills.json", new[]
        {
            Passive(100, [2]), Passive(200, [0]), Passive(300, [1]),
            Passive(10), Passive(11), Passive(12), Passive(13), Passive(14),
            Passive(20), Passive(21), Passive(30), Passive(90), Passive(97), Passive(98), Passive(99),
            Passive(70, ascendancy: true), Passive(80, ascendancy: true, grant: 3),
        });
        object Graph(bool phrecia) => new
        {
            nodes = new[]
            {
                new { id = 100, adjacent = new[] { 10 } },
                new { id = 200, adjacent = new[] { 20 } },
                new { id = 300, adjacent = new[] { 30, 70 } },
                new { id = 10, adjacent = phrecia ? new[] { 12, 97, 200 } : new[] { 11, 12, 97, 200 } },
                new { id = 11, adjacent = new[] { 13 } },
                new { id = 12, adjacent = new[] { 13 } },
                new { id = 13, adjacent = new[] { 14 } },
                new { id = 14, adjacent = new[] { 21 } }, // one mastery shared by two notables.
                new { id = 20, adjacent = new[] { 21 } },
                new { id = 21, adjacent = Array.Empty<int>() },
                new { id = 30, adjacent = Array.Empty<int>() },
                new { id = 90, adjacent = Array.Empty<int>() },
                new { id = 97, adjacent = new[] { 98 } },
                new { id = 98, adjacent = Array.Empty<int>() },
                new { id = 70, adjacent = phrecia ? Array.Empty<int>() : new[] { 80 } },
            }.Concat(phrecia ? [] : new[] { new { id = 80, adjacent = Array.Empty<int>() } }).ToArray(),
        };
        Write(AscendancyCatalog.GraphFile, new { revision = AscendancyCatalog.Revision, normal = Graph(false), phrecia = Graph(true) });
    }

    public void Dispose()
    {
        string tables = Path.Combine(_folder, "tables");
        foreach (string file in Directory.EnumerateFiles(tables)) File.Delete(file);
        Directory.Delete(tables);
        foreach (string file in Directory.EnumerateFiles(_folder)) File.Delete(file);
        Directory.Delete(_folder);
    }
    OrdinaryPassiveRules Rules => new(_folder);
    void Write(string relativePath, object value) => File.WriteAllText(Path.Combine(_folder, relativePath), JsonSerializer.Serialize(value));

    [Fact]
    public void Entire_batch_is_checked_independently_of_request_order_and_edges_are_undirected()
    {
        Rules.Validate(1, AscendancyFamily.Normal, [14, 13, 11, 10]);
        Rules.Validate(3, AscendancyFamily.Normal, [21, 20]);
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [14, 13]));
    }

    [Fact]
    public void Removing_a_bridge_is_refused_but_a_remaining_route_through_a_loop_is_allowed()
    {
        ushort[] saved = [10, 11, 12, 13, 14];
        Rules.Validate(1, AscendancyFamily.Normal, saved);
        Rules.Validate(1, AscendancyFamily.Normal, [10, 12, 13, 14]);
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 13, 14]));
        Assert.Equal(new ushort[] { 10, 11, 12, 13, 14 }, saved);
    }

    [Fact]
    public void A_mastery_is_accessible_from_either_notable_but_cannot_connect_their_separate_branches()
    {
        Rules.Validate(1, AscendancyFamily.Normal, [10, 11, 13, 14]);
        Rules.Validate(3, AscendancyFamily.Normal, [20, 21, 14]);
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 11, 13, 14, 21, 20]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(3, AscendancyFamily.Normal, [20, 21, 14, 13]));
    }

    [Fact]
    public void A_foreign_class_start_is_neither_a_free_bridge_nor_an_explicit_paid_node()
    {
        // 10 touches the Witch start, but this does not authorise crossing that unallocated start to 20.
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 20]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 200, 20]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [100, 10]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(2, AscendancyFamily.Normal, [10]));
    }

    [Fact]
    public void A_bypass_node_is_allowed_but_does_not_bridge_its_neighbours()
    {
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [20, 21]));
        // 20 may be allocated through the jewel, but 21 (outside the bypass set) is not connected to the tree.
        Assert.Throws<AscendancyRuleException>(() =>
            Rules.Validate(1, AscendancyFamily.Normal, [20, 21], bypassNodes: new HashSet<ushort> { 20 }));
        Rules.Validate(1, AscendancyFamily.Normal, [20], bypassNodes: new HashSet<ushort> { 20 });
        Rules.Validate(1, AscendancyFamily.Normal, [90], bypassNodes: new HashSet<ushort> { 90 });
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [90]));
        // A bypass node adjacent to the normal component is a normal bridge and may propagate.
        Rules.Validate(1, AscendancyFamily.Normal, [10, 11], bypassNodes: new HashSet<ushort> { 10 });
        Assert.Throws<AscendancyRuleException>(() =>
            Rules.Validate(1, AscendancyFamily.Normal, [100], bypassNodes: new HashSet<ushort> { 100 }));
        Assert.Throws<AscendancyRuleException>(() =>
            Rules.Validate(1, AscendancyFamily.Normal, [14], bypassNodes: new HashSet<ushort> { 14 }));
        Assert.Throws<AscendancyRuleException>(() =>
            Rules.Validate(1, AscendancyFamily.Normal, [97], bypassNodes: new HashSet<ushort> { 97 }));
    }

    [Fact]
    public void A_bypass_node_requires_a_connected_provider_socket()
    {
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [20],
            bypassNodes: new HashSet<ushort> { 20 },
            bypassProviders: new Dictionary<ushort, HashSet<ushort>> { [20] = [90] }));
        Rules.Validate(1, AscendancyFamily.Normal, [10, 20],
            bypassNodes: new HashSet<ushort> { 10, 20 },
            bypassProviders: new Dictionary<ushort, HashSet<ushort>> { [10] = [100], [20] = [10] });
    }

    [Fact]
    public void Duplicate_missing_and_ascendancy_nodes_are_refused_in_the_ordinary_allocation()
    {
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 10]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [99]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [ushort.MaxValue]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(0, AscendancyFamily.Normal, [70]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [90]));
    }

    [Fact]
    public void Position_proxies_and_uninstantiated_expansion_nodes_cannot_be_allocated_or_used_as_bridges()
    {
        var proxy = Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 97]));
        Assert.Contains("unsupported expansion/cluster", proxy.Message);
        var expansion = Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 98]));
        Assert.Contains("unsupported expansion/cluster", expansion.Message);
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10, 97, 98]));
    }

    [Fact]
    public void The_selected_family_determines_the_edges_not_only_node_membership()
    {
        Rules.Validate(1, AscendancyFamily.Normal, [10, 11]);
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(1, AscendancyFamily.Phrecia, [10, 11]));
        Rules.Validate(1, AscendancyFamily.Phrecia, [10, 12, 13]);
    }

    [Fact]
    public void An_allocated_ascendant_path_grants_an_additional_implicit_root_and_refunding_it_revokes_the_root()
    {
        // Character row 0 is Witch/IntegerId 3. The stat encodes 3, not row 0 or IntegerId + 1.
        Rules.Validate(0, AscendancyFamily.Normal, [20, 21], [80]);
        Rules.Validate(0, AscendancyFamily.Normal, [30, 20, 21], [80]);
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(0, AscendancyFamily.Normal, [20, 21]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(0, AscendancyFamily.Normal, [20], [70]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(0, AscendancyFamily.Normal, [20], [10]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(0, AscendancyFamily.Phrecia, [20], [80]));
        Assert.Throws<AscendancyRuleException>(() => Rules.Validate(0, AscendancyFamily.Normal, [200, 20], [80]));
    }

    [Fact]
    public void Missing_data_refuses_nonempty_allocations_but_an_empty_reset_is_safe()
    {
        File.Delete(Path.Combine(_folder, "tables", "Stats.json"));
        var rules = Rules;
        Assert.False(rules.Loaded);
        Assert.False(rules.GraphLoaded);
        rules.Validate(1, AscendancyFamily.Normal, []);
        Assert.Throws<AscendancyRuleException>(() => rules.Validate(1, AscendancyFamily.Normal, [10]));
    }

    [Fact]
    public void Unknown_graph_revision_and_dangling_edges_are_refused()
    {
        Write(AscendancyCatalog.GraphFile, new { revision = "future" });
        Assert.Throws<InvalidDataException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10]));
        Write(AscendancyCatalog.GraphFile, new
        {
            revision = AscendancyCatalog.Revision,
            normal = new { nodes = new[] { new { id = 100, adjacent = new[] { 999 } } } },
            phrecia = new { nodes = Array.Empty<object>() },
        });
        Assert.Throws<InvalidDataException>(() => Rules.Validate(1, AscendancyFamily.Normal, [10]));
    }

    [Theory]
    [InlineData(1, 31628)]
    [InlineData(3, 57226)]
    [InlineData(0, 2151)]
    [InlineData(2, 39821)]
    [InlineData(4, 39725)]
    [InlineData(6, 38129)]
    [InlineData(5, 20228)]
    public void Each_real_class_can_allocate_its_first_ordinary_node_in_both_families(byte baseClass, ushort node)
    {
        if (RealDataFolder() is not { } folder) return;
        var rules = new OrdinaryPassiveRules(folder);
        Assert.True(rules.Loaded);
        rules.Validate(baseClass, AscendancyFamily.Normal, [node]);
        rules.Validate(baseClass, AscendancyFamily.Phrecia, [node]);
    }

    [Fact]
    public void Real_ascendant_Path_of_the_Marauder_grants_the_Marauder_start_only_while_allocated()
    {
        if (RealDataFolder() is not { } folder) return;
        var rules = new OrdinaryPassiveRules(folder);
        rules.Validate(0, AscendancyFamily.Normal, [31628], [24755]);
        Assert.Throws<AscendancyRuleException>(() => rules.Validate(0, AscendancyFamily.Normal, [31628]));
        Assert.Throws<AscendancyRuleException>(() => rules.Validate(0, AscendancyFamily.Phrecia, [31628], [24755]));
    }

    [Fact]
    public void Real_Axe_Mastery_does_not_shortcut_from_Cleaving_to_Slaughter()
    {
        if (RealDataFolder() is not { } folder) return;
        var rules = new OrdinaryPassiveRules(folder);
        // The Marauder path reaches Cleaving 4940; mastery 24224 also touches Slaughter 23038.
        ushort[] path = [31628, 9511, 23881, 26523, 6446, 55649, 22285, 38508,
            37326, 48109, 63723, 25682, 20018, 6113, 9976, 4940, 24224];
        foreach (var family in Enum.GetValues<AscendancyFamily>())
        {
            rules.Validate(1, family, path);
            Assert.Throws<AscendancyRuleException>(() => rules.Validate(1, family, [.. path, 23038]));
        }
    }

    [Fact]
    public void Real_large_jewel_socket_is_available_but_raw_cluster_proxies_and_expansions_are_not()
    {
        if (RealDataFolder() is not { } folder) return;
        var rules = new OrdinaryPassiveRules(folder);
        ushort[] path = [57226, 21678, 32210, 8948, 27659, 37671, 27415,
            32710, 49605, 22618, 21033, 21984];
        foreach (var family in Enum.GetValues<AscendancyFamily>())
        {
            rules.Validate(3, family, path);
            var proxy = Assert.Throws<AscendancyRuleException>(() => rules.Validate(3, family, [.. path, 18756]));
            Assert.Contains("unsupported expansion/cluster", proxy.Message);
            var expansion = Assert.Throws<AscendancyRuleException>(() => rules.Validate(3, family, [.. path, 13170]));
            Assert.Contains("unsupported expansion/cluster", expansion.Message);
            Assert.Throws<AscendancyRuleException>(() => rules.Validate(3, family, [.. path, 18756, 13170]));
        }
    }

    static string? RealDataFolder() => File.Exists(Path.Combine("data", AscendancyCatalog.GraphFile)) &&
        File.Exists("data/tables/PassiveSkills.json") ? "data" : null;
}
