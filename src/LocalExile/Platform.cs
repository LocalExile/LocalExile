using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalExile;

/// <summary>What differs on Linux, where the servers are native and the client runs under Proton through umu-run.</summary>
static class Platform
{
    /// <summary>A program's file name.</summary>
    public static string Program(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>A file on PATH; null when there is none.</summary>
    public static string? OnPath(string file) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists);

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
