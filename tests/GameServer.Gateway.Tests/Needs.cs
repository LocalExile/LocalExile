namespace GameServer.Gateway.Tests;

// Tests of what only a set-up checkout has (Agents/GettingStarted.md, "Extract the game data"). Without the file such a
// test shows as skipped: it neither fails on a fresh clone nor passes without having run.

/// <summary>Needs data/game-files.zip, the client's level files.</summary>
sealed class GameFilesFactAttribute : FactAttribute
{
    public GameFilesFactAttribute()
    {
        if (!File.Exists(Path.Combine(TestEnvironment.Repository, "data", "game-files.zip")))
            Skip = "needs data/game-files.zip (scripts/dev/extract-data.ps1 -Servers)";
    }
}

/// <summary>Needs the client's own archive, Client/Content.ggpk.</summary>
sealed class ClientArchiveFactAttribute : FactAttribute
{
    public ClientArchiveFactAttribute()
    {
        if (!File.Exists(Path.Combine(TestEnvironment.Repository, "Client", "Content.ggpk")))
            Skip = "needs Client/Content.ggpk";
    }
}
