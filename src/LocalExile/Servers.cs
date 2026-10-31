using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LocalExile;

/// <summary>Starts Patch, Gateway and Auth from app\ with their output in logs\, as live.ps1 starts them, and waits until
/// each one listens. Their working directory is the install, so data\, logs\, Modules\ and Leagues\ resolve as in a checkout.</summary>
sealed class Servers : IDisposable
{
    readonly List<Process> _running = [];
    readonly List<StreamWriter> _logs = [];

    public static void AssertPortsFree(Ports ports)
    {
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        if (ports.All.Where(listening.Contains).ToArray() is { Length: > 0 } busy)
            throw new LauncherException(ExitCode.Busy, $"port(s) {string.Join(", ", busy)} are in use: another LocalExile may be running (LocalExile.exe stop)");
    }

    public static async Task<Servers> StartAsync(Ports ports, Output log)
    {
        var servers = new Servers();
        try
        {
            foreach (var (name, arguments, port, seconds) in new (string, string[], int, int)[]
            {
                ("Patch", [], ports.Patch, 60),
                ("Gateway", [], ports.Gateway, 600),
                ("Auth", ["serve"], ports.Login, 60),
            })
            {
                log.Step("server-" + name.ToLowerInvariant(), $"starting the {name} server{(name == "Gateway" ? " (it loads the game data; the first start is the longest)" : "")} ...");
                servers.Start(name, arguments, ports);
                await servers.WaitForListenerAsync(name, port, seconds, log);
            }
            return servers;
        }
        catch
        {
            servers.Dispose();
            throw;
        }
    }

    void Start(string name, string[] arguments, Ports ports)
    {
        Directory.CreateDirectory("logs");
        string lower = name.ToLowerInvariant();
        var output = new StreamWriter(Path.Combine("logs", $"live-{lower}.log")) { AutoFlush = true };
        _logs.Add(output);
        var start = new ProcessStartInfo(Path.GetFullPath(Path.Combine(Tools.App, $"GameServer.{name}.exe")))
        {
            WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Tools.Prepare(start);
        // Without Game_GATEWAY_ENTER the Gateway stops after creating the player and the client never enters the area.
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Game_GATEWAY_ENTER"] = "1", ["Game_PATCH_TCP_PORT"] = $"{ports.Patch}", ["Game_LOGIN_PORT"] = $"{ports.Login}",
            ["Game_AUTH_CONTROL_PORT"] = $"{ports.Control}", ["Game_GATEWAY_PORT"] = $"{ports.Gateway}",
            ["Game_PATCH_URL"] = $"http://127.0.0.1:{ports.PatchWeb}", ["Game_PACKET_DUMPS"] = @"logs\packets",
        }) start.Environment[key] = value;
        var process = new Process { StartInfo = start };
        void Write(string? line) { if (line is not null) lock (output) output.WriteLine(line); }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _running.Add(process);
    }

    async Task WaitForListenerAsync(string name, int port, int seconds, Output log)
    {
        var process = _running[^1];
        string file = Path.Combine("logs", $"live-{name.ToLowerInvariant()}.log");
        var clock = Stopwatch.StartNew();
        var report = TimeSpan.FromSeconds(10);
        while (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port))
        {
            if (process.HasExited)
                throw new LauncherException(ExitCode.Server, $"the {name} server stopped while starting (exit {process.ExitCode}; {file}):\n{Tools.Tail(file)}");
            if (clock.Elapsed.TotalSeconds > seconds)
                throw new LauncherException(ExitCode.Server, $"the {name} server did not start in {seconds} s ({file}):\n{Tools.Tail(file)}");
            if (clock.Elapsed > report)
            {
                report = clock.Elapsed + TimeSpan.FromSeconds(10);
                string last = Tools.Tail(file).Split('\n')[^1];
                log.Progress("server-" + name.ToLowerInvariant(), $"still starting {name} ({(int)clock.Elapsed.TotalSeconds} s){(last.Length > 0 ? ": " + (last.Length > 100 ? last[..100] + "..." : last) : "")}");
            }
            await Task.Delay(250);
        }
        log.Info($"{name} is up after {(int)clock.Elapsed.TotalSeconds} s");
    }

    public Task WaitForAnyExitAsync() => Task.WhenAny(_running.Select(p => p.WaitForExitAsync()));

    public void Dispose()
    {
        foreach (var process in _running)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        foreach (var output in _logs) lock (output) output.Dispose();
    }

    /// <summary>"stop": every process that runs from this install, except this one.</summary>
    public static void StopInstall(string root, Output log)
    {
        int stopped = 0;
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        string client = Path.GetFullPath("Client") + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                if (path is null || process.Id == Environment.ProcessId) continue;
                // A linked client's path is outside the install: it is told by its name.
                bool ours = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(client, StringComparison.OrdinalIgnoreCase)
                    || (Path.GetFileName(path).Equals("Game.patched.exe", StringComparison.OrdinalIgnoreCase) && SameFile(path, Path.Combine(client, "Game.patched.exe")));
                if (!ours) continue;
                process.Kill(entireProcessTree: true);
                stopped++;
            }
        log.Done(stopped == 0 ? "nothing was running" : $"stopped {stopped} process(es)");
    }

    static bool SameFile(string a, string b) =>
        File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length && File.GetLastWriteTimeUtc(a) == File.GetLastWriteTimeUtc(b);
}

/// <summary>A job that ends every process in it when its last handle closes: when this process ends, however it ends.
/// What this process starts joins it by itself.</summary>
static class KillOnExitJob
{
    const int ExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32", SetLastError = true)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32")] static extern nint GetCurrentProcess();

    public static void AddCurrentProcess()
    {
        nint job = CreateJobObject(0, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (job == 0 || !SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
            || !AssignProcessToJobObject(job, GetCurrentProcess()))
            throw new InvalidOperationException($"could not create the process job (error {Marshal.GetLastWin32Error()})");
    }
}
