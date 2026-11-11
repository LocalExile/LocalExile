using System.Runtime.CompilerServices;

namespace GameServer.Gateway.Tests;

static class TestEnvironment
{
    // Production catalogs' paths are relative to the repository root. Set this once before lazy catalogs initialize, rather
    // than changing the process's working directory in a test.
    [ModuleInitializer]
    internal static void Initialize()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(SourceFile())! })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))
                    && File.Exists(Path.Combine(dir.FullName, "src", "GameServer.Gateway", "GameServer.Gateway.csproj")))
                {
                    Directory.SetCurrentDirectory(dir.FullName);
                    // The client tables come from data/client.db (ClientTables), built from sql/ as a server does.
                    GameServer.Storage.SqlUpdater.Update(GameServer.Core.ClientTables.DefaultPath, "client", log: _ => { });
                    // The game content (settings, catalogues, drop tables) from a scratch world.db, as the Gateway opens its own.
                    GameData.Open(ScratchWorld);
                    Saves.Open(ScratchSaves);
                    return;
                }
        throw new DirectoryNotFoundException("Gateway tests need a LocalExile checkout.");
    }

    // Isolated --artifacts-path output can be a sibling of the checkout, with no repository among its ancestors.
    static string SourceFile([CallerFilePath] string path = "") => path;

    // The save store (Saves) is one static: a test that opens its own and disposes it leaves the next test that reaches
    // a save (a character's inventories, loaded once per run) with a closed connection, and the collections' order
    // varies from run to run. A test that opened its own store releases it through here: the scratch store takes its
    // place (Saves.Open disposes the one before it) and the test's files go.
    internal static string ScratchWorld => Path.Combine(Path.GetTempPath(), "localexile-gateway-tests-world.db");

    // Not the name the tests used before the databases were built from sql/: a checkout from then leaves (and keeps
    // writing) a file of that name with tables but no updates record, which the updater cannot bring up to date.
    internal static string ScratchSaves => Path.Combine(Path.GetTempPath(), "localexile-gateway-tests-characters.db");

    internal static void ReleaseSaves(string path)
    {
        Saves.Open(ScratchSaves);
        // The accounts database that goes with it (SaveDatabase.AuthPathFor) goes too.
        foreach (string file in new[] { path, GameServer.Storage.SaveDatabase.AuthPathFor(path) })
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(file + suffix);
    }
}
