using System.Runtime.CompilerServices;

namespace GameServer.Core.Tests;

/// <summary>The league modules' declarations, from their source, as the Auth server reads them once they are built.</summary>
static class TestLeagues
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string mods = Path.Combine(Path.GetTempPath(), "localexile-core-test-modules-" + Environment.ProcessId);
        string source = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "Modules");
        foreach (string module in new[] { "standard", "ruthless", "allflame" })
        {
            Directory.CreateDirectory(Path.Combine(mods, module));
            File.Copy(Path.Combine(source, module, "module.json"), Path.Combine(mods, module, "module.json"), overwrite: true);
        }
        Environment.SetEnvironmentVariable("Game_MODULES_FOLDER", mods);
    }

    static string Here([CallerFilePath] string path = "") => path;
}
