using System.Runtime.CompilerServices;

namespace GameServer.Core.Tests;

/// <summary>The league modules' declarations, from their source, as the Auth server reads them once they are built.</summary>
static class TestLeagues
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string mods = Path.Combine(Path.GetTempPath(), "localexile-core-test-mods-" + Environment.ProcessId);
        string source = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "Mods", "Source");
        foreach (string module in new[] { "Ruthless", "Allflame" })
        {
            Directory.CreateDirectory(Path.Combine(mods, module.ToLowerInvariant()));
            File.Copy(Path.Combine(source, module, "mod.json"), Path.Combine(mods, module.ToLowerInvariant(), "mod.json"), overwrite: true);
        }
        Environment.SetEnvironmentVariable("Game_MODS_FOLDER", mods);
    }

    static string Here([CallerFilePath] string path = "") => path;
}
