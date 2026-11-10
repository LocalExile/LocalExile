using System.Diagnostics;

namespace LocalExile;

/// <summary>The only way a release starts the client. Started any other way (the original executable, or without
/// --patching-server), the client contacts the official patch servers and can update itself, which ends the setup.</summary>
static class ClientLaunch
{
    static string Folder => GameServer.Core.ServerConfig.ClientFolder;
    const string Executable = "Game.patched.exe";

    static string[] Arguments(int patchPort) => ["--patching-server", $"127.0.0.1:{patchPort}"];

    /// <summary>What <see cref="Start"/> runs; the release build refuses a package where this is not as it should be.</summary>
    public static string CommandLine(int patchPort) => string.Join(' ', [Path.Combine(Folder, Executable), .. Arguments(patchPort)]);

    public static void AssertNoneRunning()
    {
        if (OperatingSystem.IsWindows() ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)).Length > 0 : Platform.WineClients().Any())
            throw new LauncherException(ExitCode.Busy, "the client is already running; close it first");
    }

    public static Process Start(int patchPort, Output log)
    {
        log.Step("client", "starting the client ...");
        string folder = Path.GetFullPath(Folder);
        var start = new ProcessStartInfo(Path.Combine(folder, Executable)) { WorkingDirectory = folder, UseShellExecute = false };
        foreach (string argument in Arguments(patchPort)) start.ArgumentList.Add(argument);
        if (!OperatingSystem.IsWindows()) UnderProton(start);
        var client = Process.Start(start) ?? throw new LauncherException(ExitCode.ClientStart, "the client did not start");
        if (!OperatingSystem.IsWindows()) Follow(client);
        if (client.WaitForExit(1000))
            throw new LauncherException(ExitCode.ClientStart, $"the client exited right after starting (exit code {client.ExitCode}); see Client\\logs\\Client.txt");
        return client;
    }

    /// <summary>The client through umu-run, in this install's own prefix unless the shell names one (a launcher such as Waypoint
    /// keeps the settings elsewhere). A first start sets up the prefix and may download Proton.</summary>
    static void UnderProton(ProcessStartInfo start)
    {
        string runner = Environment.GetEnvironmentVariable("Game_CLIENT_RUNNER") is { Length: > 0 } own ? own : "umu-run";
        if (Platform.OnPath(runner) is null) throw new LauncherException(ExitCode.ClientStart, $"{runner} was not found: install umu-launcher, which runs the client under Proton");
        start.ArgumentList.Insert(0, start.FileName);
        start.FileName = runner;
        string prefix = Path.GetFullPath(Path.Combine("data", "wine-prefix"));
        Directory.CreateDirectory(prefix);
        foreach (var (name, value) in new[] { ("WINEPREFIX", prefix), ("PROTONPATH", "GE-Proton"), ("GAMEID", "umu-238960") })
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) start.Environment[name] = value;
        start.Environment["PROTON_VERB"] = "run";
        start.RedirectStandardOutput = start.RedirectStandardError = true;
    }

    /// <summary>What Proton prints goes to logs\live-client.log: standard output is this launcher's own.</summary>
    static void Follow(Process client)
    {
        Directory.CreateDirectory("logs");
        var output = new StreamWriter(Path.Combine("logs", "live-client.log")) { AutoFlush = true };
        void Write(string? line) { if (line is not null) lock (output) output.WriteLine(line); }
        client.OutputDataReceived += (_, e) => Write(e.Data);
        client.ErrorDataReceived += (_, e) => Write(e.Data);
        client.BeginOutputReadLine();
        client.BeginErrorReadLine();
        EndWithThisProcess.Add(client);
    }
}
