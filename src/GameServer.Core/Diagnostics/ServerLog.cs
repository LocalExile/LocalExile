using System.Net.Sockets;
using System.Text;

namespace GameServer.Core.Diagnostics;

/// <summary>
/// The servers' console log (live.ps1 redirects it to logs\live-*.log). Install() puts the local time (HH:mm:ss.fff) in
/// front of every line of stdout and stderr, so the Gateway's log lines up with the client's Client.txt, Frida traces
/// (scripts/re/frida/trace-crash.js prints the same clock) and the other servers' logs, and writes any unhandled
/// exception, a fire-and-forget task's included, whole. Game_LOG_TIMESTAMPS=0 leaves the lines as they are.
/// </summary>
public static class ServerLog
{
    static int installed;

    public static void Install(string name)
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        if (Environment.GetEnvironmentVariable("Game_LOG_TIMESTAMPS") != "0")
        {
            Console.SetOut(TextWriter.Synchronized(new TimestampedWriter(Console.Out)));
            Console.SetError(TextWriter.Synchronized(new TimestampedWriter(Console.Error)));
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine($"[{name}] unhandled exception: {e.ExceptionObject}");
        // A task nobody awaited (`_ = HandleAsync(...)`) that failed: reported when the task is collected. On stdout: the
        // server keeps running, and live.ps1 reads anything on stderr at startup as a failed start.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.WriteLine($"[{name}] unobserved task exception: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>
    /// An exception for a log line: the type and message for what a closing connection throws, the whole exception with
    /// its stack trace (and inner exceptions) for anything else, which is a bug to find.
    /// </summary>
    public static string Describe(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException
            ? $"{ex.GetType().Name}: {ex.Message}"
            : ex.ToString();
}

/// <summary>Writes the time at the start of every line to another writer. Not thread-safe: wrap it in TextWriter.Synchronized.</summary>
public sealed class TimestampedWriter(TextWriter inner, Func<DateTime>? clock = null) : TextWriter
{
    readonly Func<DateTime> now = clock ?? (() => DateTime.Now);
    bool lineStart = true;

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value) => Write(value.ToString());

    // Console's own writer flushes after every write, so the text goes out in one write with its stamps.
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!lineStart && value.IndexOf('\n') < 0)
        {
            inner.Write(value);
            return;
        }
        var text = new StringBuilder(value.Length + 16);
        int from = 0;
        while (from < value.Length)
        {
            if (lineStart) text.Append(now().ToString("HH:mm:ss.fff "));
            int end = value.IndexOf('\n', from);
            int to = end < 0 ? value.Length : end + 1;
            text.Append(value, from, to - from);
            lineStart = end >= 0;
            from = to;
        }
        inner.Write(text.ToString());
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine(string? value) => Write(value + NewLine);

    public override void Flush() => inner.Flush();
}

/// <summary>
/// The dev traces: log lines too many for every run, on with Game_TRACE=&lt;category&gt;[,…] or Game_TRACE=all.
/// Categories: move (movement messages with QPC stamps), latency (per-phase timings), ticks (every 50th 0x1b tick),
/// ai (monster chase moves), packets (every message in both directions, hex). The older Game_MOVE_TRACE=1 and
/// Game_LATENCY_METRICS=1 still turn on move and latency.
/// </summary>
public static class ServerTrace
{
    static readonly HashSet<string> On = Parse(Environment.GetEnvironmentVariable("Game_TRACE"));

    public static readonly bool Move = Enabled("move") || Environment.GetEnvironmentVariable("Game_MOVE_TRACE") == "1";
    public static readonly bool Latency = Enabled("latency") || Environment.GetEnvironmentVariable("Game_LATENCY_METRICS") == "1";
    public static readonly bool Ticks = Enabled("ticks");
    public static readonly bool Ai = Enabled("ai");
    public static readonly bool Packets = Enabled("packets");

    public static bool Enabled(string category) => On.Contains("all") || On.Contains(category);

    public static HashSet<string> Parse(string? value) =>
        (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant()).ToHashSet();
}
