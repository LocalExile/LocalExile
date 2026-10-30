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

    static string? FindData() => File.Exists(Path.Combine("data", AscendancyCatalog.GraphFile)) ? "data" : null;
}

[Collection(TestSaves.FailWriteCollection)]
public sealed class AscendancySaveTests
{
    public AscendancySaveTests() => TestSaves.Ensure();
    static string Character() => $"Asc{Guid.NewGuid():N}"[..20];
    static AscendancyProgress Progress() => new(new(AscendancyFamily.Phrecia, "Slayer", AscendancyCatalog.Revision), [54114, 48846], ["normal"]);

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
    public void Dispose()
    {
        string tables = Path.Combine(Folder, "tables");
        foreach (string file in Directory.EnumerateFiles(tables)) File.Delete(file);
        Directory.Delete(tables);
        foreach (string file in Directory.EnumerateFiles(Folder)) File.Delete(file);
        Directory.Delete(Folder);
    }
}
