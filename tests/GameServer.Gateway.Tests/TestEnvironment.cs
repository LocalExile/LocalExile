using System.Runtime.CompilerServices;
using GameServer.Core;

namespace GameServer.Gateway.Tests;

static class TestEnvironment
{
    // Production catalogs' paths are relative to the repository root. Set this once before lazy catalogs initialize, rather
    // than changing the process's working directory in a test.
    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.SetCurrentDirectory(Repository);
        RemoveEndedRuns();
        Directory.CreateDirectory(Run);
        // The client tables come from data/client.db (ClientTables), built from sql/ as a server does.
        GameServer.Storage.SqlUpdater.Update(GameServer.Core.ClientTables.DefaultPath, "client", log: _ => { });
        // The game content (settings, catalogues, drop tables) from a scratch world.db, as the Gateway opens its own.
        GameData.Open(ScratchWorld);
        // A test that loads a character's inventories expects a save store; the test order is not stable, so
        // open the scratch one before any test and let ReleaseSaves return to it.
        Saves.Open(ScratchSaves);
    }

    internal static readonly string Repository = FindRepository();

    static string FindRepository()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(SourceFile())! })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))
                    && File.Exists(Path.Combine(dir.FullName, "src", "GameServer.Gateway", "GameServer.Gateway.csproj")))
                    return dir.FullName;
        throw new DirectoryNotFoundException("Gateway tests need a LocalExile checkout.");
    }

    // Isolated --artifacts-path output can be a sibling of the checkout, with no repository among its ancestors.
    static string SourceFile([CallerFilePath] string path = "") => path;

    // Each run has its own folder for the databases it makes. One fixed file for every checkout on the machine meant
    // one branch's run met the updates another branch's run had recorded, and the updater refuses a file that changed.
    static readonly string Runs = Path.Combine(Path.GetTempPath(), "localexile-gateway-tests");
    static readonly string Run = Path.Combine(Runs, $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    internal static string ScratchWorld => Path.Combine(Run, "world.db");

    // The save store (Saves) is one static: a test that opens its own and disposes it leaves the next test that reaches
    // a save (a character's inventories, loaded once per run) with a closed connection, and the collections' order
    // varies from run to run. A test that opened its own store releases it through ReleaseSaves or ReleaseOwnSaves: the
    // scratch store takes its place (Saves.Open disposes the one before it) and the test's files go.
    internal static string ScratchSaves => Path.Combine(Run, "characters.db");

    // The stores are open until the process ends, so a run cannot remove its own folder: the next run removes the
    // folders of runs whose process is gone.
    static void RemoveEndedRuns()
    {
        if (!Directory.Exists(Runs)) return;
        foreach (string run in Directory.GetDirectories(Runs))
        {
            if (int.TryParse(Path.GetFileName(run).Split('-')[0], out int id) && Running(id)) continue;
            try { Directory.Delete(run, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static bool Running(int id)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    static readonly List<string> Own = [];

    // A save store of the test's own, in the run's folder. The test class releases it in Dispose (ReleaseOwnSaves).
    internal static void OpenOwnSaves(string name)
    {
        string path = Path.Combine(Run, $"{name}-{Guid.NewGuid():N}.db");
        Own.Add(path);
        Saves.Open(path);
    }

    internal static void ReleaseOwnSaves()
    {
        foreach (string path in Own.ToArray()) ReleaseSaves(path);
        Own.Clear();
    }

    internal static void ReleaseSaves(string path)
    {
        Saves.Open(ScratchSaves);
        // The accounts database that goes with it (SaveDatabase.AuthPathFor) goes too.
        foreach (string file in new[] { path, GameServer.Storage.SaveDatabase.AuthPathFor(path) })
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(file + suffix);
    }
}
