using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Imports;
using GameServer.Storage;
using Microsoft.Data.Sqlite;

namespace GameServer.Gateway.Tests;

public class PathOfBuildingCharacterTests
{
    [Fact]
    public void Prepared_progress_preserves_level_mastery_and_only_required_extra_points()
    {
        using var data = new Data();
        var progress = Prepare(Build(nodes: [10, 11, 12], masteries: new Dictionary<ushort, ushort> { [12] = 500 }), data.Folder);
        Assert.Equal(CharacterClass.Witch, progress.Class);
        Assert.Equal(2, progress.Level);
        Assert.Equal(1000u, progress.Experience);
        Assert.Equal(new ushort[] { 11, 12 }, progress.Passives);
        Assert.Equal((ushort)500, progress.MasteryEffects[12]);
        Assert.Equal(1, progress.ExtraPassivePoints);
    }

    [Theory]
    [InlineData("3_28")]
    [InlineData("3_30")]
    public void Another_game_version_is_refused(string version)
    {
        using var data = new Data();
        var build = Build() with { Tree = Build().Tree with { Version = version } };
        Assert.Contains("requires 3_29", Assert.Throws<ArgumentException>(() => Prepare(build, data.Folder)).Message);
    }

    [Fact]
    public void Unsupported_or_invalid_tree_choices_are_refused_before_any_save()
    {
        using var data = new Data();
        var ordinary = Build();
        var invalid = new[]
        {
            ordinary with { Tree = ordinary.Tree with { ClassId = 1 } },
            ordinary with { Tree = ordinary.Tree with { AscendancyId = 1 } },
            ordinary with { Ascendancy = "Elementalist" },
            ordinary with { Tree = ordinary.Tree with { SecondaryAscendancyId = 1 } },
            ordinary with { Tree = ordinary.Tree with { HasOverrides = true } },
            ordinary with { Tree = ordinary.Tree with { Jewels = [new(11, 1)] } },
            ordinary with { Bandit = "Alira" }, ordinary with { PantheonMajor = "Lunaris" },
            ordinary with { PantheonMinor = "Ryslatha" },
            Build(nodes: [11, 11]), Build(nodes: [65536]), Build(nodes: [999]),
            Build(nodes: [13]), Build(nodes: [14]), Build(nodes: [15]), Build(nodes: [16]),
            Build(nodes: [12]),
            Build(nodes: [11], masteries: new Dictionary<ushort, ushort> { [11] = 500 }),
            Build(nodes: [12], masteries: new Dictionary<ushort, ushort> { [12] = 501 }),
        };
        foreach (var build in invalid)
            Assert.Throws<ArgumentException>(() => Prepare(build, data.Folder));
    }

    [Fact]
    public void Extra_passive_point_budget_is_bounded()
    {
        using var data = new Data();
        var nodes = Enumerable.Range(100, 25).Select(n => (uint)n).ToList();
        Assert.Equal(24, Prepare(Build(nodes: nodes), data.Folder).ExtraPassivePoints);
        nodes.Add(125);
        Assert.Contains("quest budget is 24", Assert.Throws<ArgumentException>(() =>
            Prepare(Build(nodes: nodes), data.Folder)).Message);
    }

    [Fact]
    public void Missing_experience_data_does_not_create_a_wrong_level_character()
    {
        using var data = new Data();
        File.WriteAllText(Path.Combine(data.Folder, "tables", "ExperienceLevels.json"), "[]");
        Assert.Contains("experience", Assert.Throws<ArgumentException>(() =>
            Prepare(Build(), data.Folder)).Message);
    }

    [Fact]
    public void New_character_and_complete_save_survive_reopen_without_starter_overwrite()
    {
        using var data = new Data();
        using (var db = new SaveDatabase(data.Database))
        {
            Register(db);
            var progress = Prepare(Build(nodes: [11]), data.Folder);
            var save = Save(progress) with { Inventories = new() { [2] = [new(1, 0, 0, new(123))] } };
            var character = PathOfBuildingCharacters.Create(db, "import_account", "ImportedWitch", progress, save, "SSF Standard");
            Assert.Equal(2, character.Level);
            Assert.Equal("1_1_town", character.LastTown);
            Assert.Equal("1_1_town", character.AreaId);
            Assert.Equal("SSF Standard", character.League);
        }
        using var reopened = new SaveDatabase(data.Database);
        var stored = new SqliteAccountStore(reopened).FindCharacter("ImportedWitch");
        Assert.NotNull(stored);
        Assert.Equal(CharacterClass.Witch, stored.Class);
        var restored = JsonSerializer.Deserialize<CharacterSaves.Save>(reopened.Get(SaveDatabase.Table.Characters, "ImportedWitch")!);
        Assert.NotNull(restored);
        Assert.Equal(1000u, restored.Experience);
        Assert.Equal(new ushort[] { 11 }, restored.Passives);
        Assert.Equal(123u, Assert.Single(restored.Inventories[2]).Item.Type);
        Assert.Null(restored.Tutorial);
        Assert.Null(restored.StartWeapon);
    }

    [Fact]
    public void Failure_writing_progress_rolls_back_character_creation_too()
    {
        using var data = new Data();
        using var db = new SaveDatabase(data.Database);
        Register(db);
        using (var connection = new SqliteConnection($"Data Source={data.Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_import BEFORE INSERT ON characters BEGIN SELECT RAISE(ABORT, 'save failed'); END";
            command.ExecuteNonQuery();
        }
        var progress = Prepare(Build(), data.Folder);
        Assert.Throws<SqliteException>(() => PathOfBuildingCharacters.Create(db, "import_account", "RollbackWitch", progress, Save(progress)));
        Assert.Null(new SqliteAccountStore(db).FindCharacter("RollbackWitch"));
        Assert.Null(db.Get(SaveDatabase.Table.Characters, "RollbackWitch"));
    }

    [Fact]
    public void Existing_character_or_orphan_save_is_never_replaced()
    {
        using var data = new Data();
        using var db = new SaveDatabase(data.Database);
        Register(db);
        var progress = Prepare(Build(), data.Folder);
        PathOfBuildingCharacters.Create(db, "import_account", "ExistingWitch", progress, Save(progress));
        string original = db.Get(SaveDatabase.Table.Characters, "ExistingWitch")!;
        Assert.Throws<ArgumentException>(() => PathOfBuildingCharacters.Create(db, "import_account", "existingwitch", progress, Save(progress)));
        Assert.Equal(original, db.Get(SaveDatabase.Table.Characters, "ExistingWitch"));
        db.Put(SaveDatabase.Table.Characters, "OrphanWitch", "{\"preserve\":true}");
        Assert.Throws<ArgumentException>(() => PathOfBuildingCharacters.Create(db, "import_account", "OrphanWitch", progress, Save(progress)));
        Assert.Equal("{\"preserve\":true}", db.Get(SaveDatabase.Table.Characters, "OrphanWitch"));
        Assert.Null(new SqliteAccountStore(db).FindCharacter("OrphanWitch"));
    }

    [Fact]
    public void Creation_checks_account_name_limit_and_supported_league()
    {
        using var data = new Data();
        using var db = new SaveDatabase(data.Database);
        var account = Register(db);
        var progress = Prepare(Build(), data.Folder);
        var save = Save(progress);
        Assert.Throws<ArgumentException>(() => PathOfBuildingCharacters.Create(db, "missing", "TestWitch", progress, save));
        Assert.Throws<ArgumentException>(() => PathOfBuildingCharacters.Create(db, account.Name, "x", progress, save));
        Assert.Throws<ArgumentException>(() => PathOfBuildingCharacters.Create(db, account.Name, "TestWitch", progress, save, "MissingLeague"));
        Assert.Throws<ArgumentException>(() => PathOfBuildingCharacters.Create(db, account.Name, "TestWitch", progress, save, "Ruthless"));
        var service = new AccountService(new SqliteAccountStore(db));
        for (int i = 0; i < 24; i++) Assert.True(service.CreateCharacter(account.Id, $"Character{i}", CharacterClass.Witch).Ok);
        Assert.Contains("LimitReached", Assert.Throws<ArgumentException>(() =>
            PathOfBuildingCharacters.Create(db, account.Name, "OverLimitWitch", progress, save)).Message);
        Assert.Null(db.Get(SaveDatabase.Table.Characters, "OverLimitWitch"));
    }

    [Fact]
    public void Legacy_character_saves_are_not_shadowed_by_an_import()
    {
        using var data = new Data();
        using var db = new SaveDatabase(data.Database);
        Register(db);
        Directory.CreateDirectory(Path.Combine(data.Folder, "saves"));
        string legacy = Path.Combine(data.Folder, "saves", "LegacyWitch.json");
        File.WriteAllText(legacy, "{\"keep\":true}");
        var progress = Prepare(Build(), data.Folder);
        Assert.Contains("legacy save", Assert.Throws<ArgumentException>(() =>
            PathOfBuildingCharacters.Create(db, "import_account", "legacywitch", progress, Save(progress))).Message);
        Assert.Equal("{\"keep\":true}", File.ReadAllText(legacy));
        Assert.Null(new SqliteAccountStore(db).FindCharacter("LegacyWitch"));
        Assert.Null(db.Get(SaveDatabase.Table.Characters, "LegacyWitch"));
    }

    static CharacterSaves.Save Save(PathOfBuildingCharacters.Progress progress) => new([], Experience: progress.Experience,
        Passives: progress.Passives, ExtraPassivePoints: progress.ExtraPassivePoints, MasteryEffects: progress.MasteryEffects);

    static Account Register(SaveDatabase db) => new AccountService(new SqliteAccountStore(db)).Register("import_account", "local_password").Value!;

    static PathOfBuildingCharacters.Progress Prepare(PathOfBuildingBuild build, string folder) =>
        PathOfBuildingCharacters.Prepare(build, folder, () => new Dictionary<uint, uint[]>
        {
            [10] = [11, .. Enumerable.Range(100, 26).Select(id => (uint)id)],
            [11] = [10, 12], [12] = [11],
        }.Concat(Enumerable.Range(100, 26).Select(id => new KeyValuePair<uint, uint[]>((uint)id, [10])))
            .ToDictionary(p => p.Key, p => p.Value));

    static PathOfBuildingBuild Build(IReadOnlyList<uint>? nodes = null, IReadOnlyDictionary<ushort, ushort>? masteries = null) =>
        new(CharacterClass.Witch, 2, new(3, 0, "3_29", nodes ?? [], masteries ?? new Dictionary<ushort, ushort>(), [], false), [], [], [], "None");

    sealed class Data : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"localexile-pob-{Guid.NewGuid():N}");
        public string Database => Path.Combine(Folder, "saves.db");

        public Data()
        {
            Directory.CreateDirectory(Path.Combine(Folder, "tables"));
            void Write(string table, object value) => File.WriteAllText(Path.Combine(Folder, "tables", table + ".json"), JsonSerializer.Serialize(value));
            Write("Characters", new[] { new { IntegerId = 3 } });
            Write("ExperienceLevels", Enumerable.Range(1, 100).Select(l => new { Level = l, Experience = l == 1 ? 0 : (l - 1) * 1000 }));
            Dictionary<string, object?> Node(int id, int[]? characters = null, int? mastery = null, int? ascendancy = null, bool expansion = false, int skillType = 0) => new()
            {
                ["Id"] = $"node{id}", ["PassiveSkillGraphId"] = id, ["Name"] = $"Node {id}",
                ["Characters"] = characters ?? [], ["Stats"] = Array.Empty<int>(), ["SkillPointsGranted"] = 0,
                ["IsExpansion"] = expansion, ["IsMultipleChoice"] = false, ["__col26"] = 0,
                ["MasteryGroup"] = mastery, ["AscendancyKey"] = ascendancy, ["SkillType"] = skillType,
            };
            Write("PassiveSkills", new[] { Node(10, [0]), Node(11), Node(12, mastery: 0), Node(13, ascendancy: 0),
                Node(14, expansion: true), Node(15, skillType: 1), Node(16, [1]) }.Concat(Enumerable.Range(100, 26).Select(i => Node(i))));
            Write("PassiveSkillMasteryEffects", new[] { new { HASH16 = 500, Id = "test_mastery", Stats = Array.Empty<int>() } });
            Write("PassiveSkillMasteryGroups", new[] { new { MasteryEffects = new[] { 0 } } });
        }

        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }
}
