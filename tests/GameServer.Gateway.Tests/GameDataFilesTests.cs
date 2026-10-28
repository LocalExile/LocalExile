namespace GameServer.Gateway.Tests;

public class GameDataFilesTests
{
    [Fact]
    public void Warning_names_every_missing_file_and_points_at_extract_data()
    {
        string dir = Directory.CreateTempSubdirectory("gamedata").FullName;
        try
        {
            Assert.NotNull(GameDataFiles.Warning(dir));
            foreach (var (file, _) in GameDataFiles.All.Where(f => f.File != "data/hideouts.json"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, file))!);
                File.WriteAllText(Path.Combine(dir, file), "{}");
            }
            string? warning = GameDataFiles.Warning(dir);
            Assert.NotNull(warning);
            Assert.Contains("1 game data file(s) missing", warning);
            Assert.Contains("data/hideouts.json (hideouts and native stationary model collision radii)", warning);
            Assert.Contains("extract-data.ps1", warning);
            File.WriteAllText(Path.Combine(dir, "data/hideouts.json"), "{}");
            Assert.Null(GameDataFiles.Warning(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Every_file_the_Gateway_checks_has_an_entry_in_the_extract_data_manifest()
    {
        string? root = TestEnvironment.RepositoryRoot;
        while (root is not null && !File.Exists(Path.Combine(root, "scripts", "dev", "extract-data.ps1"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string script = File.ReadAllText(Path.Combine(root, "scripts", "dev", "extract-data.ps1"));
        foreach (var (file, _) in GameDataFiles.All)
            Assert.Contains($"'{file.Replace('/', '\\')}'", script);
    }

    [Fact]
    public void The_program_catalogs_find_the_tables_from_the_shared_repository_root()
    {
        // TestEnvironment sets the repository root before Program initializes its relative production catalogs.
        // All three suppression keys resolve when Stats is present, and stay empty without the extracted table.
        bool stats = File.Exists(Path.Combine("data", "tables", "Stats.json"));
        Assert.Equal(stats ? 3 : 0, Program.SuppressionChanceKeys.Length);
        if (stats)
            Assert.True(File.Exists(Path.Combine("data", "tables", "MonsterTypes.json")));
    }
}
