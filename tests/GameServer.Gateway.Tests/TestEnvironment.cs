using System.Reflection;
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
                    return;
                }
        throw new DirectoryNotFoundException("Gateway tests need a LocalExile checkout.");
    }

    // Isolated --artifacts-path output can be a sibling of the checkout, with no repository among its ancestors.
    static string SourceFile([CallerFilePath] string path = "") => path;

    /// <summary>The game's rules (the Game module, Agents/docs/dev/modules.md), loaded once for the test run as the Gateway
    /// loads them: the module host starts on the built modules in Modules/ (build the solution first), without watching
    /// them, so a test never builds or reloads a module a live Gateway uses. Every reused stat snapshot is checked against
    /// a fresh one (Game_STAT_SNAPSHOT_VERIFY). The saves are a temporary database, deleted when the run ends.</summary>
    internal static Assembly GameRules => Rules.Value;

    static readonly Lazy<Assembly> Rules = new(() =>
    {
        Environment.SetEnvironmentVariable("Game_STAT_SNAPSHOT_VERIFY", "1");
        string saves = Path.Combine(Path.GetTempPath(), $"localexile-tests-{Guid.NewGuid():N}.db");
        Saves.Open(saves);
        Program.ServerModules.Start(watch: false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Program.ServerModules.Dispose();
            Saves.Db.Dispose();
            foreach (string file in new[] { saves, saves + "-wal", saves + "-shm" })
                try { File.Delete(file); } catch (IOException) { }
        };
        return Program.GameLogicOrNull?.GetType().Assembly
            ?? throw new InvalidOperationException($"the Game module did not load from {Program.ModulesFolder}/: build the solution first");
    });
}
