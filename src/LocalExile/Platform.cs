using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalExile;

/// <summary>What differs on Linux, where the servers are native and the client runs under Proton through umu-run.</summary>
static class Platform
{
    /// <summary>A program's file name.</summary>
    public static string Program(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>A file on PATH; null when there is none.</summary>
    public static string? OnPath(string file) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists);

    /// <summary>The prefix the client runs in: one of LocalExile's own, so its settings and saved login outlive any install and
    /// every install has them, and no other Path of Exile ever sees them. Login keys are in data\, so nothing there depends on it.</summary>
    public static string GamePrefix => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "LocalExile", "wine-prefix");

    /// <summary>LocalExile's first settings, in the prefix it chose (one the shell names is the player's, and never written): this
    /// install's from before the prefix was shared, when it has them, and the network mode the server needs. Once the file exists
    /// nothing here touches it, so what the player sets in game stays. scripts/dev/platform-linux.ps1 does the same.</summary>
    public static void InitializeGameSettings(string prefix)
    {
        const string Documents = "drive_c/users/steamuser/Documents/My Games/Path of Exile", Config = "production_Config.ini";
        string folder = Path.Combine(prefix, Documents), settings = Path.Combine(folder, Config);
        if (File.Exists(settings)) return;
        Directory.CreateDirectory(folder);
        string old = Path.GetFullPath(Path.Combine("data", "wine-prefix", Documents));
        foreach (string name in new[] { "production_login.credentials", Config })
            if (File.Exists(Path.Combine(old, name))) File.Copy(Path.Combine(old, name), Path.Combine(folder, name));
        string text = File.Exists(settings) ? File.ReadAllText(settings) : "";
        text = NetworkMode().IsMatch(text) ? NetworkMode().Replace(text, "networking_type=predictive")
            : text.TrimEnd() + "\r\n[NETWORKING]\r\nnetworking_type=predictive\r\n";
        // The byte-order mark is how the client writes this file.
        File.WriteAllText(settings, text.TrimStart(), new UTF8Encoding(true));
    }

    static Regex NetworkMode() => new(@"(?m)^networking_type=[^\r\n]*");

    /// <summary>The Windows clients running on this machine under Wine: their PIDs. A client shows its Windows path as its first
    /// argument, whose drive letter Proton picks, so it is told by that file's name and by the folder it runs in.</summary>
    public static IEnumerable<(int Pid, string? Folder)> WineClients()
    {
        foreach (string folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), out int pid)) continue;
            string first;
            try { first = File.ReadAllText(folder + "/cmdline").Split('\0')[0]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (!first.EndsWith("Game.patched.exe", StringComparison.OrdinalIgnoreCase) || !first.Contains('\\')) continue;
            string? working = null;
            try { working = new DirectoryInfo(folder + "/cwd").ResolveLinkTarget(false)?.FullName; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            yield return (pid, working);
        }
    }
}

/// <summary>Outside Windows there are no job objects: what this process started ends when a signal ends it. Unlike a job this
/// cannot cover SIGKILL; `LocalExile stop` still finds what is left.</summary>
static class EndWithThisProcess
{
    static readonly List<Process> Started = [];
    static readonly List<PosixSignalRegistration> Signals = [];

    public static void Add(Process process) { lock (Started) Started.Add(process); }

    public static void Arrange()
    {
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            Signals.Add(PosixSignalRegistration.Create(signal, _ => EndAll()));
    }

    static void EndAll()
    {
        lock (Started)
            foreach (var process in Started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
    }
}
