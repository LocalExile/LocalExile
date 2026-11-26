using GameServer.Core.Accounts;

namespace GameServer.Core.Tests;

/// <summary>A release unzipped over 0.6.0-beta (RetiredFolders, LocalExile.exe): the old modules and leagues move aside,
/// the bots' state first to where the playerbots mod reads it.</summary>
public sealed class RetiredFoldersTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"retired-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    void Write(string name, string text = "")
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void An_install_updated_in_place_moves_the_old_modules_aside_and_keeps_the_bots()
    {
        Write("Modules/Custom/PlayerBots/module.json");
        Write("Modules/Custom/PlayerBots/state/bots.json", "[\"Xander582\"]");
        Write("Modules/Legacy/Idols/module.json");
        Write("Modules/Legacy/Content/HarvestHeartOfTheGrove/module.json");
        Write("Leagues/Phrecia/league.json");
        Write("Leagues/Standard/league.json");
        Write("Modules/Core/Game/module.json");
        var said = new List<string>();
        var now = new DateTime(2026, 11, 27, 10, 0, 0);

        RetiredFolders.MoveAside(_root, now, said.Add, said.Add);

        Assert.Equal("[\"Xander582\"]", File.ReadAllText(Path.Combine(_root, "data/mods/playerbots/bots.json")));
        string backup = Path.Combine(_root, "backups/retired-20261127-100000");
        Assert.True(File.Exists(Path.Combine(backup, "Modules/Custom/PlayerBots/state/bots.json")));
        Assert.True(File.Exists(Path.Combine(backup, "Leagues/Phrecia/league.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Leagues/Phrecia")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Modules/Legacy")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Modules/Custom")));
        Assert.True(File.Exists(Path.Combine(_root, "Leagues/Standard/league.json")));
        Assert.True(File.Exists(Path.Combine(_root, "Modules/Core/Game/module.json")));
        Assert.Equal(5, said.Count);

        // Once moved, nothing is left to do; a state the mod already has is not replaced.
        said.Clear();
        Write("Modules/Custom/PlayerBots/state/bots.json", "[]");
        RetiredFolders.MoveAside(_root, now.AddDays(1), said.Add, said.Add);
        Assert.Equal("[\"Xander582\"]", File.ReadAllText(Path.Combine(_root, "data/mods/playerbots/bots.json")));
        Assert.Single(said);
    }

    [Fact]
    public void An_install_from_before_0_4_moves_its_older_places_too_and_the_newest_bots_state_wins()
    {
        // A copy from a run whose move failed: while an old module of that id is there, its state is newer than this one.
        Write("data/mods/playerbots/bots.json", "[\"snapshot\"]");
        Write("Modules/Custom/PlayerBots/module.json");
        Write("Modules/Custom/PlayerBots/state/bots.json", "[\"custom\"]");
        Write("Modules/PlayerBots/module.json");
        Write("Modules/PlayerBots/state/bots.json", "[\"flat\"]");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "Modules/Custom/PlayerBots/state/bots.json"), DateTime.UtcNow.AddDays(-1));
        Write("Modules/Ruthless/module.json");
        Write("Modules/Core/Ruthless/module.json");
        var warned = new List<string>();

        RetiredFolders.MoveAside(_root, new DateTime(2026, 11, 27), _ => { }, warned.Add);

        Assert.Empty(warned);
        Assert.Equal("[\"flat\"]", File.ReadAllText(Path.Combine(_root, "data/mods/playerbots/bots.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Modules/PlayerBots")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Modules/Ruthless")));
        Assert.True(File.Exists(Path.Combine(_root, "Modules/Core/Ruthless/module.json")));
    }
}
