using System.Diagnostics;

namespace LocalExile;

/// <summary>The only way a release starts the client. Started any other way (the original executable, or without
/// --patching-server), the client contacts the official patch servers and can update itself, which ends the setup.</summary>
static class ClientLaunch
{
    const string Folder = "Client";
    const string Executable = "Game.patched.exe";

    static string[] Arguments(int patchPort) => ["--patching-server", $"127.0.0.1:{patchPort}"];

    /// <summary>What <see cref="Start"/> runs; the release build refuses a package where this is not as it should be.</summary>
    public static string CommandLine(int patchPort) => string.Join(' ', [Path.Combine(Folder, Executable), .. Arguments(patchPort)]);

    public static void AssertNoneRunning()
    {
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)).Length > 0)
            throw new LauncherException(ExitCode.Busy, "the client is already running; close it first");
    }

    public static Process Start(int patchPort, Output log)
    {
        log.Step("client", "starting the client ...");
        string folder = Path.GetFullPath(Folder);
        var start = new ProcessStartInfo(Path.Combine(folder, Executable)) { WorkingDirectory = folder, UseShellExecute = false };
        foreach (string argument in Arguments(patchPort)) start.ArgumentList.Add(argument);
        var client = Process.Start(start) ?? throw new LauncherException(ExitCode.ClientStart, "the client did not start");
        if (client.WaitForExit(1000))
            throw new LauncherException(ExitCode.ClientStart, $"the client exited right after starting (exit code {client.ExitCode}); see Client\\logs\\Client.txt");
        return client;
    }
}
