using System.Runtime.CompilerServices;

namespace GameServer.Gateway.Tests;

static class TestEnvironment
{
    // Real dispatch tests use Program's production catalogs, whose paths are relative to the repository root.
    // Set this once before parallel tests or lazy catalogs initialize, rather than changing process CWD in a test.
    [ModuleInitializer]
    internal static void Initialize()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(SourceFile())! })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))
                    && File.Exists(Path.Combine(dir.FullName, "src", "GameServer.Gateway", "GameServer.Gateway.csproj"))
                    && Directory.Exists(Path.Combine(dir.FullName, "data", "tables")))
                {
                    Directory.SetCurrentDirectory(dir.FullName);
                    // The league modules, as the mod host and Auth see them in the server, whether or not they are built.
                    string mods = Path.Combine(Path.GetTempPath(), "localexile-test-mods-" + Environment.ProcessId);
                    foreach (string module in new[] { "Ruthless", "Allflame" })
                    {
                        Directory.CreateDirectory(Path.Combine(mods, module.ToLowerInvariant()));
                        File.Copy(Path.Combine(dir.FullName, "Mods", "Source", module, "mod.json"), Path.Combine(mods, module.ToLowerInvariant(), "mod.json"), overwrite: true);
                    }
                    Environment.SetEnvironmentVariable("Game_MODS_FOLDER", mods);
                    ServerLeagues.Add(new GameServer.Mods.Ruthless.RuthlessLeague(Path.Combine("config", "ruthless.json")));
                    ServerLeagues.Add(new GameServer.Mods.Allflame.AllflameLeague());
                    return;
                }
        throw new DirectoryNotFoundException("Gateway tests need a LocalExile checkout with data/tables.");
    }

    // Isolated --artifacts-path output can be a sibling of the checkout, with no repository among its ancestors.
    static string SourceFile([CallerFilePath] string path = "") => path;
}
