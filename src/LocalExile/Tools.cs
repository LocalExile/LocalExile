using System.Diagnostics;

namespace LocalExile;

/// <summary>Runs the release's own programs, with the bundled Node and Python found first and output in logs\.
/// While one works, a line every 10 s says so, with the newest line of its output.</summary>
static class Tools
{
    public const string App = "app";
    public static string Auth => Path.Combine(App, "GameServer.Auth.exe");
    public static string Patch => Path.Combine(App, "GameServer.Patch.exe");
    public static string Python => Path.GetFullPath(Path.Combine("Tools", "python", "python.exe"));

    /// <summary>The child environment of everything here: tools call "node" and the generators the file dumper.</summary>
    public static void Prepare(ProcessStartInfo start)
    {
        start.Environment["PATH"] = $"{Path.GetFullPath(Path.Combine("Tools", "node"))};{Environment.GetEnvironmentVariable("PATH")}";
        start.Environment["Game_GGDUMP_EXE"] = Path.GetFullPath(Path.Combine(App, "GgDump.exe"));
    }

    static ProcessStartInfo Info(string exe, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(exe))
        {
            WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Prepare(start);
        return start;
    }

    /// <summary>Exit code and every line of output, for short commands whose output is the answer.</summary>
    public static (int Code, List<string> Lines) Capture(string exe, IEnumerable<string> arguments, string? input = null)
    {
        using var process = Process.Start(Info(exe, arguments))!;
        if (input is not null) process.StandardInput.WriteLine(input);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var lines = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        process.WaitForExit();
        lines.AddRange(error.Result.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return (process.ExitCode, lines);
    }

    /// <summary>Runs one tool to its end with its output in logs\<paramref name="name"/>.log; the exit code.</summary>
    public static int Run(string exe, IEnumerable<string> arguments, string name, Output log)
    {
        Directory.CreateDirectory("logs");
        string path = Path.Combine("logs", name + ".log");
        using var process = new Process { StartInfo = Info(exe, arguments) };
        using var writer = new StreamWriter(path) { AutoFlush = true };
        string? newest = null;
        void Take(string? line)
        {
            if (line is null) return;
            lock (writer) { writer.WriteLine(line); }
            if (line.Length > 0) newest = line;
        }
        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var clock = Stopwatch.StartNew();
        while (!process.WaitForExit(10_000))
        {
            string shown = newest is { Length: > 100 } ? newest[..100] + "..." : newest ?? "";
            log.Progress(name, $"still working ({(int)clock.Elapsed.TotalSeconds} s){(shown.Length > 0 ? ": " + shown : "")}");
        }
        process.WaitForExit();
        return process.ExitCode;
    }

    public static void RunOrThrow(string exe, IEnumerable<string> arguments, string name, ExitCode failure, Output log, string? message = null)
    {
        int code = Run(exe, arguments, name, log);
        if (code == 0) return;
        throw new LauncherException(failure, $"{message ?? name + " failed"} (exit {code}; logs\\{name}.log):\n{Tail(Path.Combine("logs", name + ".log"))}");
    }

    public static string Tail(string path) =>
        File.Exists(path) ? string.Join('\n', ReadShared(path).TakeLast(15)) : "";

    static IEnumerable<string> ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
}
