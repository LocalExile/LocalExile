using System.Text.Json;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class AscendancyTests : IDisposable
{
    readonly AscendancyFixture _data = new();
    AscendancyCatalog Catalog => new(_data.Folder);
    static AscendancyIdentity Identity(AscendancyFamily family = AscendancyFamily.Normal, string id = "Slayer", string revision = AscendancyCatalog.Revision) => new(family, id, revision);
    public void Dispose() => _data.Dispose();

    [Fact]
    public void Both_families_are_additional_choices_and_use_character_integer_id_not_row()
    {
        var choices = Catalog.Choices(4).ToArray();
        Assert.Equal(2, choices.Length);
        Assert.Equal(new[] { "Slayer", "Aristocrat" }, choices.Select(d => d.Name));
        Assert.All(choices, d => Assert.Equal((byte)4, d.BaseClass));
        Assert.Empty(Catalog.Choices(1));
        Assert.Equal((byte)1, choices[0].ClassNo);
        Assert.Equal(choices[0].StartNode, choices[1].StartNode);
        Assert.NotEqual(choices[0].Identity, choices[1].Identity);
    }

    [Fact]
    public void Phrecia_scion_aliases_do_not_create_duplicate_scavengers()
    {
        Assert.Equal(3, Catalog.Choices(0).Count());
        Assert.Single(Catalog.Choices(0), d => d.Identity.Family == AscendancyFamily.Phrecia);
        Assert.Equal("Scavenger", Catalog.Find(0, AscendancyFamily.Phrecia, "scavenger")!.Name);
        Assert.Equal("Aristocrat", Catalog.Find(4, AscendancyFamily.Phrecia, "Slayer")!.Name);
    }

    [Fact]
    public void Membership_is_from_each_actual_graph_and_never_the_shared_table_owner_alone()
    {
        var normal = Catalog.Find(Identity())!;
        var phrecia = Catalog.Find(Identity(AscendancyFamily.Phrecia))!;
        Assert.True(normal.Nodes.ContainsKey(11));
        Assert.False(normal.Nodes.ContainsKey(101));
        Assert.True(phrecia.Nodes.ContainsKey(101));
        Assert.False(phrecia.Nodes.ContainsKey(11));
        Assert.Equal(5, phrecia.Nodes[102].PointsGranted);
    }

    [Fact]
    public void Awards_are_separate_from_ordinary_points_and_earned_once_per_tier()
    {
        var rules = new AscendancyRules(Catalog);
        AscendancyProgress? progress = null;
        foreach (string award in AscendancyRules.AwardKeys)
        {
            progress = rules.Award(4, progress, award);
            Assert.Same(progress, rules.Award(4, progress, award));
        }
        Assert.Equal(8, progress!.EarnedPoints);
        Assert.Null(progress.Selection);
        Assert.Throws<AscendancyRuleException>(() => rules.Award(4, progress, "made-up"));
        Assert.Throws<AscendancyRuleException>(() => rules.Validate(4, progress with { Awards = ["normal", "normal"] }));
        Assert.Throws<AscendancyRuleException>(() => rules.Validate(4, progress with { Awards = ["new-future-tier"] }));
    }

    [Fact]
    public void Unknown_revision_identity_or_wrong_base_is_rejected_without_changing_the_original()
    {
        var rules = new AscendancyRules(Catalog);
        var original = new AscendancyProgress(Awards: ["normal"]);
        Assert.Throws<AscendancyRuleException>(() => rules.Select(0, original, Identity()));
        Assert.Throws<AscendancyRuleException>(() => rules.Select(4, original, Identity(revision: "3.30")));
        Assert.Throws<AscendancyRuleException>(() => rules.Select(4, original, Identity(id: "Missing")));
        Assert.Null(original.Selection);
        Assert.Empty(original.Allocated);
        Assert.Equal(2, original.EarnedPoints);
    }

    [Fact]
    public void Allocation_requires_the_selected_family_connectivity_and_budget()
    {
        var rules = new AscendancyRules(Catalog);
        var selected = rules.Select(4, new(Awards: ["normal"]), Identity());
        var allocated = rules.Allocate(4, selected, [11, 12]);
        Assert.Equal(2, rules.SpentPoints(4, allocated));
        Assert.Equal(5, rules.OrdinaryPointsGranted(4, allocated));
        Assert.Equal(2, allocated.EarnedPoints);
        Assert.Throws<AscendancyRuleException>(() => rules.Allocate(4, selected, [12]));
        Assert.Throws<AscendancyRuleException>(() => rules.Allocate(4, selected, [101]));
        Assert.Throws<AscendancyRuleException>(() => rules.Allocate(4, selected, [10]));
        Assert.Throws<AscendancyRuleException>(() => rules.Allocate(4, selected, [11, 11]));
        Assert.Throws<AscendancyRuleException>(() => rules.Allocate(4, allocated, [13]));
        Assert.Throws<AscendancyRuleException>(() => rules.Validate(4, new(Nodes: [11])));
        Assert.Empty(selected.Allocated);
    }

    [Fact]
    public void Refunding_cannot_orphan_nodes_or_spend_removed_ordinary_point_grants()
    {
        var rules = new AscendancyRules(Catalog);
        var allocated = rules.Allocate(4, rules.Select(4, new(Awards: ["normal"]), Identity()), [11, 12]);
        Assert.Throws<AscendancyRuleException>(() => rules.Refund(4, allocated, [11], 0, 2));
        Assert.Throws<AscendancyRuleException>(() => rules.Refund(4, allocated, [12], 6, 2));
        Assert.Throws<AscendancyRuleException>(() => rules.Refund(4, allocated, [13], 0, 2));
        var refund = rules.Refund(4, allocated, [12], 2, 2);
        Assert.Equal(5, refund.Cost);
        Assert.Equal(new ushort[] { 11 }, refund.Progress.Allocated);
        Assert.Equal(0, rules.OrdinaryPointsGranted(4, refund.Progress));
        Assert.Equal(new ushort[] { 11, 12 }, allocated.Allocated);
    }

    [Fact]
    public void A_second_main_ascendancy_requires_refunding_the_first_and_keeps_earned_points()
    {
        var rules = new AscendancyRules(Catalog);
        var allocated = rules.Allocate(4, rules.Select(4, new(Awards: ["normal"]), Identity()), [11, 12]);
        Assert.Throws<AscendancyRuleException>(() => rules.Select(4, allocated, Identity(AscendancyFamily.Phrecia)));
        var empty = rules.Refund(4, allocated, [11, 12], 0, 0).Progress;
        var phrecia = rules.Select(4, empty, Identity(AscendancyFamily.Phrecia));
        Assert.Equal(2, phrecia.EarnedPoints);
        Assert.Empty(phrecia.Allocated);
        Assert.Equal(AscendancyFamily.Phrecia, phrecia.Selection!.Family);
    }

    [Fact]
    public void Multiple_choice_effects_require_their_paid_parent_and_are_exclusive()
    {
        var rules = new AscendancyRules(Catalog);
        var selected = rules.Select(0, new(Awards: ["normal"]), Identity(id: "Ascendant"));
        var chosen = rules.Allocate(0, selected, [21, 22]);
        Assert.Equal(1, rules.SpentPoints(0, chosen));
        Assert.Throws<AscendancyRuleException>(() => rules.Allocate(0, selected, [22]));
        Assert.Throws<AscendancyRuleException>(() => rules.Allocate(0, chosen, [23]));
        Assert.Throws<AscendancyRuleException>(() => rules.Refund(0, chosen, [21], 0, 0));
        Assert.Equal(5, rules.Refund(0, chosen, [21, 22], 0, 0).Cost);
        // Native 0x141efb0e0 charges the selected effect, not the shell that consumed the point. Otherwise every
        // Scavenger/Ascendant/Polytheist option could be swapped for free while its paid parent remains allocated.
        var effectRefund = rules.Refund(0, chosen, [22], 0, 0);
        Assert.Equal(5, effectRefund.Cost);
        Assert.Equal(1, rules.SpentPoints(0, effectRefund.Progress));
        Assert.Equal(0, rules.Refund(0, effectRefund.Progress, [21], 0, 0).Cost);
        Assert.Equal(1, rules.SpentPoints(0, rules.Allocate(0, effectRefund.Progress, [23])));
    }

    [Fact]
    public void Missing_graph_data_never_authorises_nodes_or_invents_choices()
    {
        File.Delete(Path.Combine(_data.Folder, AscendancyCatalog.GraphFile));
        var catalog = Catalog;
        Assert.False(catalog.Loaded);
        Assert.Empty(catalog.Definitions);
        Assert.Throws<AscendancyRuleException>(() => new AscendancyRules(catalog).Select(4, null, Identity()));
    }

    [Fact]
    public void Future_graph_revision_is_not_silently_loaded_as_this_client()
    {
        string path = Path.Combine(_data.Folder, AscendancyCatalog.GraphFile);
        File.WriteAllText(path, File.ReadAllText(path).Replace(AscendancyCatalog.Revision, "3.30"));
        Assert.Throws<InvalidDataException>(() => Catalog.Loaded);
    }

    [Fact]
    public void Exact_client_data_exposes_twenty_one_normal_and_nineteen_additional_choices_when_extracted()
    {
        string? folder = FindData();
        if (folder is null) return;
        var catalog = new AscendancyCatalog(folder);
        Assert.Equal(21, catalog.Definitions.Count(d => d.Identity.Family == AscendancyFamily.Normal));
        Assert.Equal(19, catalog.Definitions.Count(d => d.Identity.Family == AscendancyFamily.Phrecia));
        Assert.Equal(6, catalog.Choices(4).Count());
        var aristocrat = catalog.Find(4, AscendancyFamily.Phrecia, "Aristocrat")!;
        Assert.Equal((ushort)33795, aristocrat.StartNode);
        Assert.Equal(5, aristocrat.Nodes[48846].PointsGranted);
        Assert.Contains(aristocrat.Nodes[30851].Stats, s => s == (15621u, 20));
        Assert.False(catalog.Find(4, AscendancyFamily.Normal, "Slayer")!.Nodes.ContainsKey(30851));
    }

    static string? FindData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string folder = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(folder, AscendancyCatalog.GraphFile))) return folder;
        }
        return null;
    }
}

[Collection(TestSaves.FailWriteCollection)]
public sealed class AscendancySaveTests
{
    public AscendancySaveTests() => TestSaves.Ensure();
    static string Character() => $"Asc{Guid.NewGuid():N}"[..20];
    static AscendancyProgress Progress() => new(new(AscendancyFamily.Phrecia, "Slayer", AscendancyCatalog.Revision), [54114, 48846], ["normal"]);

    [Fact]
    public void Identity_nodes_awards_and_refund_payment_round_trip_with_ordinary_progress_and_unknown_fields()
    {
        string character = Character();
        Saves.Db.Put(SaveDatabase.Table.Characters, character,
            """{"Inventories":{},"Experience":17,"Passives":[1],"RefundPoints":10,"Ascendancy":{"Class":"Elementalist"},"FutureField":[1,2]}""");
        CharacterSaves.SetMainAscendancy(character, Progress(), [1, 2], new Dictionary<ushort, ushort> { [2] = 3 }, 5);
        var save = CharacterSaves.Load(character)!;
        Assert.Equal(17u, save.Experience);
        Assert.Equal(new ushort[] { 1, 2 }, save.Passives);
        Assert.Equal((ushort)3, save.MasteryEffects![2]);
        Assert.Equal((ushort)5, save.RefundPoints);
        Assert.Equal(Progress().Selection, save.MainAscendancy!.Selection);
        Assert.Equal(new ushort[] { 54114, 48846 }, save.MainAscendancy.Allocated);
        Assert.Equal(new[] { "normal" }, save.MainAscendancy.EarnedAwards);
        Assert.Contains("Elementalist", save.Unknown!["Ascendancy"].GetRawText());
        Assert.Equal("[1,2]", save.Unknown["FutureField"].GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""));
        CharacterSaves.SetRefundPoints(character, 7);
        Assert.Equal(Progress().Selection, CharacterSaves.Load(character)!.MainAscendancy!.Selection);
    }

    [Fact]
    public void Old_saves_remain_unascended_and_keep_their_ordinary_allocations()
    {
        string character = Character();
        Saves.Db.Put(SaveDatabase.Table.Characters, character, """{"Inventories":{},"Passives":[1,2],"ExtraPassivePoints":3}""");
        var save = CharacterSaves.Load(character)!;
        Assert.Null(save.MainAscendancy);
        Assert.Equal(new ushort[] { 1, 2 }, save.Passives);
        CharacterSaves.SetMainAscendancy(character, new(Awards: ["normal"]));
        Assert.Equal(new ushort[] { 1, 2 }, CharacterSaves.Load(character)!.Passives);
        Assert.Equal((ushort)3, CharacterSaves.Load(character)!.ExtraPassivePoints);
    }

    [Fact]
    public void A_failed_save_changes_neither_selection_nor_allocations_nor_payment()
    {
        string character = Character();
        CharacterSaves.Write(character, new([], Passives: [1], RefundPoints: 10));
        string before = Saves.Get(SaveDatabase.Table.Characters, character)!;
        Saves.FailWrite = name => name == character;
        try
        {
            Assert.Throws<IOException>(() => CharacterSaves.SetMainAscendancy(character, Progress(), [2], new Dictionary<ushort, ushort>(), 5));
        }
        finally { Saves.FailWrite = null; }
        Assert.Equal(before, Saves.Get(SaveDatabase.Table.Characters, character));
        Assert.Null(CharacterSaves.Load(character)!.MainAscendancy);
        Assert.Equal((ushort)10, CharacterSaves.Load(character)!.RefundPoints);
    }

    [Fact]
    public void A_later_failure_in_the_outer_transaction_rolls_back_the_entire_ascendancy_save()
    {
        string character = Character();
        CharacterSaves.Write(character, new([], Passives: [1], RefundPoints: 10));
        string before = Saves.Get(SaveDatabase.Table.Characters, character)!;
        Assert.Throws<IOException>(() => Saves.Atomically(() =>
        {
            CharacterSaves.SetMainAscendancy(character, Progress(), [2], new Dictionary<ushort, ushort>(), 5);
            throw new IOException("another part of the operation failed");
        }));
        Assert.Equal(before, Saves.Get(SaveDatabase.Table.Characters, character));
    }

    [Fact]
    public void Concurrent_awards_use_the_latest_save_and_are_not_overwritten_by_an_old_area()
    {
        string character = Character();
        var rules = new AscendancyRules(new AscendancyCatalog(Path.Combine(Path.GetTempPath(), "no-ascendancy-data")));
        // Awards can be earned before choosing a class. Every worker represents an area with an old connection copy.
        Parallel.ForEach(AscendancyRules.AwardKeys, tier =>
        {
            for (int retry = 0; retry < 5; retry++)
                CharacterSaves.ChangeMainAscendancy(character, latest => rules.Award(4, latest, tier));
        });
        var progress = CharacterSaves.Load(character)!.MainAscendancy!;
        Assert.Equal(8, progress.EarnedPoints);
        Assert.Equal(AscendancyRules.AwardKeys, progress.EarnedAwards);
        Assert.Null(progress.Selection);
    }

    [Fact]
    public void A_failed_atomic_award_returns_no_publishable_state_and_keeps_the_previous_awards()
    {
        string character = Character();
        CharacterSaves.SetMainAscendancy(character, new(Awards: ["normal"]));
        string before = Saves.Get(SaveDatabase.Table.Characters, character)!;
        var rules = new AscendancyRules(new AscendancyCatalog(Path.Combine(Path.GetTempPath(), "no-ascendancy-data")));
        Saves.FailWrite = name => name == character;
        try
        {
            Assert.Throws<IOException>(() => CharacterSaves.ChangeMainAscendancy(character, latest => rules.Award(4, latest, "cruel")));
        }
        finally { Saves.FailWrite = null; }
        Assert.Equal(before, Saves.Get(SaveDatabase.Table.Characters, character));
        Assert.Equal(2, CharacterSaves.Load(character)!.MainAscendancy!.EarnedPoints);
    }

    [Fact]
    public void A_stale_connection_cannot_overwrite_a_newer_allocation_or_refund_payment()
    {
        string character = Character();
        CharacterSaves.Write(character, new([], Passives: [1], RefundPoints: 10, MainAscendancy: Progress()));
        var first = CharacterSaves.Load(character)!;
        var second = CharacterSaves.Load(character)!; // independent objects, as after a second connection/area load.
        var expectedFirst = CharacterSaves.PassiveProgressSnapshot.From(first);
        var expectedSecond = CharacterSaves.PassiveProgressSnapshot.From(second);
        CharacterSaves.SetMainAscendancy(character, first.MainAscendancy! with { Nodes = [54114] },
            [1, 2], new Dictionary<ushort, ushort> { [2] = 3 }, 5, expectedFirst);
        string winner = Saves.Get(SaveDatabase.Table.Characters, character)!;
        Assert.Throws<PassiveProgressConflictException>(() => CharacterSaves.SetMainAscendancy(character,
            second.MainAscendancy! with { Nodes = [54114, 30851] }, [1, 3], new Dictionary<ushort, ushort>(), 10, expectedSecond));
        Assert.Equal(winner, Saves.Get(SaveDatabase.Table.Characters, character));
        Assert.Equal((ushort)5, CharacterSaves.Load(character)!.RefundPoints);
        Assert.Equal(new ushort[] { 54114 }, CharacterSaves.Load(character)!.MainAscendancy!.Allocated);
    }

    [Fact]
    public void Compare_and_swap_merges_unrelated_changes_and_accepts_legacy_null_defaults()
    {
        string character = Character();
        CharacterSaves.Write(character, new([], Experience: 17));
        var expected = new CharacterSaves.PassiveProgressSnapshot(new(), [], new Dictionary<ushort, ushort>(), 0);
        CharacterSaves.AddWaypoints(character, ["1_1_town"]);
        CharacterSaves.SetMainAscendancy(character, new(Awards: ["normal"]), expected: expected);
        var save = CharacterSaves.Load(character)!;
        Assert.Equal(17u, save.Experience);
        Assert.Equal(new[] { "1_1_town" }, save.Waypoints);
        Assert.Equal(2, save.MainAscendancy!.EarnedPoints);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("awards")]
    [InlineData("ordinary")]
    [InlineData("mastery")]
    [InlineData("refunds")]
    public void Every_passive_state_component_participates_in_the_atomic_compare(string component)
    {
        string character = Character();
        CharacterSaves.Write(character, new([], Passives: [1], RefundPoints: 10, MainAscendancy: Progress()));
        var initial = CharacterSaves.Load(character)!;
        var expected = CharacterSaves.PassiveProgressSnapshot.From(initial);
        var changed = component switch
        {
            "selection" => initial with { MainAscendancy = Progress() with { Selection = new(AscendancyFamily.Normal, "Slayer", AscendancyCatalog.Revision) } },
            "awards" => initial with { MainAscendancy = Progress() with { Awards = ["normal", "cruel"] } },
            "ordinary" => initial with { Passives = [1, 2] },
            "mastery" => initial with { MasteryEffects = new() { [1] = 3 } },
            _ => initial with { RefundPoints = 9 },
        };
        CharacterSaves.Write(character, changed);
        string before = Saves.Get(SaveDatabase.Table.Characters, character)!;
        Assert.Throws<PassiveProgressConflictException>(() => CharacterSaves.SetMainAscendancy(character, Progress(), expected: expected));
        Assert.Equal(before, Saves.Get(SaveDatabase.Table.Characters, character));
    }

    [Fact]
    public void Snapshot_copies_runtime_collections_instead_of_observing_later_mutations()
    {
        string character = Character();
        var passives = new List<ushort> { 1 };
        var masteries = new Dictionary<ushort, ushort> { [1] = 2 };
        var nodes = new List<ushort> { 54114 };
        var awards = new List<string> { "normal" };
        var progress = new AscendancyProgress(Progress().Selection, nodes, awards);
        CharacterSaves.Write(character, new([], Passives: [1], MasteryEffects: new() { [1] = 2 }, MainAscendancy: progress));
        var expected = new CharacterSaves.PassiveProgressSnapshot(progress, passives, masteries, 0);
        passives.Add(2); masteries[1] = 3; nodes.Add(48846); awards.Add("cruel");
        // The captured original still matches disk even though every collection in the connection changed meanwhile.
        CharacterSaves.SetMainAscendancy(character, Progress(), expected: expected);
        Assert.Equal(2, CharacterSaves.Load(character)!.MainAscendancy!.EarnedPoints);
    }
}

sealed class AscendancyFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"ascendancy-{Guid.NewGuid():N}");

    public AscendancyFixture()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "tables"));
        Write("tables/Characters.json", new[] { new { IntegerId = 4 }, new { IntegerId = 0 } });
        Write("tables/Ascendancy.json", new[]
        {
            new { Id = "Slayer", ClassNo = 1, Character = 0, Name = "Slayer", LeagueName = "Aristocrat" },
            new { Id = "Ascendant", ClassNo = 1, Character = 1, Name = "Ascendant", LeagueName = "Scavenger" },
            new { Id = "Reliquarian", ClassNo = 2, Character = 1, Name = "Reliquarian", LeagueName = "Scavenger" },
        });
        var passives = new List<object>();
        void Node(int id, int owner, bool start = false, int grant = 0, bool choice = false, bool option = false) => passives.Add(new
        {
            Id = $"Passive{id}", Name = $"Node{id}", PassiveSkillGraphId = id, AscendancyKey = owner,
            IsAscendancyStartingNode = start, SkillPointsGranted = grant, IsMultipleChoice = choice,
            IsMultipleChoiceOption = option, Stats = Array.Empty<int>(), Stat1Value = 0, Stat2Value = 0, Stat3Value = 0, Stat4Value = 0, Stat5Value = 0,
        });
        Node(10, 0, start: true); Node(11, 0); Node(12, 0, grant: 5); Node(13, 0);
        Node(101, 0); Node(102, 0, grant: 5); Node(103, 0);
        Node(20, 1, start: true); Node(21, 1, choice: true); Node(22, 1, option: true); Node(23, 1, option: true);
        Node(201, 1, choice: true); Node(202, 1, option: true); Node(203, 1, option: true);
        Node(30, 2, start: true); Node(31, 2);
        Write("tables/PassiveSkills.json", passives);
        static object Graph(params (int From, int To)[] edges)
        {
            var nodes = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().ToDictionary(id => id, _ => new HashSet<int>());
            foreach (var (from, to) in edges) { nodes[from].Add(to); nodes[to].Add(from); }
            return new { nodes = nodes.Select(n => new { id = n.Key, adjacent = n.Value }) };
        }
        Write(AscendancyCatalog.GraphFile, new
        {
            revision = AscendancyCatalog.Revision,
            normal = Graph((10, 11), (11, 12), (12, 13), (20, 21), (21, 22), (21, 23), (30, 31)),
            phrecia = Graph((10, 101), (101, 102), (102, 103), (20, 201), (201, 202), (201, 203)),
        });
    }

    void Write(string file, object value) => File.WriteAllText(Path.Combine(Folder, file), JsonSerializer.Serialize(value));
    public void Dispose() => Directory.Delete(Folder, recursive: true);
}
