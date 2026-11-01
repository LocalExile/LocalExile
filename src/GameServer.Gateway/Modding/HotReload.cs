using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(HotReload))]

/// <summary>.NET hot reload (live.ps1 runs the Gateway under `dotnet watch` unless -NoHotReload): the runtime calls UpdateApplication
/// after it applied a saved edit to the running Gateway. A method's new body runs from its next call; a loop already
/// inside the old body keeps it until it returns. Static initializers do not run again, so an edited catalog field keeps
/// the value it was built with until a restart.
/// Every player in game is told in chat when a saved source file starts a reload and when it is applied (with the time
/// from the save). The runtime has no callback before an update, so the start is a watcher on src\ that runs only under
/// `dotnet watch` (DOTNET_WATCH=1). A failed compile or a needed restart is only in `dotnet watch`'s own output, which
/// live.ps1 writes to the file named by Game_HOT_RELOAD_LOG (logs\live-gateway.err): its new lines are read here, and
/// players are told the compile errors (the old code keeps running) or that the Gateway restarts.</summary>
static class HotReload
{
    static int count;
    static long pendingSince;   // Stopwatch timestamp of the first save not yet applied; 0 = none
    static readonly ConcurrentDictionary<ClientState, byte> Players = new();
    // A save that changes nothing applies nothing; after this long the next save announces a new start.
    static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(2);
    // How many compile errors a chat message lists.
    const int ShownErrors = 3;
    static long outputRead;     // bytes of Game_HOT_RELOAD_LOG already read
    static readonly Lock Reading = new();
    static readonly FileSystemWatcher? Sources = WatchSources();
    static readonly Timer? WatchOutput = ReadWatchOutput();

    /// <summary>A connection in game that hears about reloads (Program.Connection, after its chat commands).</summary>
    internal static void Register(ClientState conn)
    {
        if (Sources is null) return;
        Players[conn] = 0;
    }

    static FileSystemWatcher? WatchSources()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_WATCH") != "1" || !Directory.Exists("src")) return null;
        var watcher = new FileSystemWatcher("src", "*.cs") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        watcher.Changed += (_, e) => OnSourceSaved(e.FullPath);
        watcher.Created += (_, e) => OnSourceSaved(e.FullPath);
        watcher.Renamed += (_, e) => OnSourceSaved(e.FullPath);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    static Timer? ReadWatchOutput()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_WATCH") != "1" ||
            Environment.GetEnvironmentVariable("Game_HOT_RELOAD_LOG") is not { Length: > 0 } path) return null;
        // What this run's `dotnet watch` wrote before the Gateway started (the build) is not news.
        outputRead = File.Exists(path) ? new FileInfo(path).Length : 0;
        return new Timer(_ => ReadNewOutput(path), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    static void ReadNewOutput(string path)
    {
        if (!Reading.TryEnter()) return;
        try { ReadNewLines(path); }
        finally { Reading.Exit(); }
    }

    static void ReadNewLines(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= outputRead) return;
            // cmd's redirect keeps the file open for writing.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Position = outputRead;
            using var reader = new StreamReader(file, System.Text.Encoding.UTF8);
            text = reader.ReadToEnd();
            // Only whole lines: the rest is read again with the next poll.
            int end = text.LastIndexOf('\n') + 1;
            outputRead += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, end));
            text = text[..end];
        }
        catch (IOException) { return; }
        List<string> errors = [];
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("error CS", StringComparison.Ordinal)) errors.Add(CompileError(line));
            // A save back to the running code (a fixed compile error, a whitespace edit) applies nothing.
            else if (line.Contains("No managed code changes to apply", StringComparison.Ordinal))
            {
                if (Interlocked.Exchange(ref pendingSince, 0) == 0) continue;
                Console.WriteLine("[gateway] hot reload: no code changes to apply");
                Tell("Hot-reload: no code changes to apply, the running code is current.");
            }
            else if (line.Contains("Restart is needed to apply the changes", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref pendingSince, 0);
                Console.WriteLine("[gateway] hot reload: the edit needs a restart; dotnet watch restarts the Gateway");
                Tell("Hot-reload cannot apply this edit: the Gateway restarts now. Log in again.");
            }
        }
        if (errors.Count == 0) return;
        // A failed save leaves the old code running; the next save announces a new start.
        Interlocked.Exchange(ref pendingSince, 0);
        string[] distinct = [.. errors.Distinct()];
        Console.WriteLine($"[gateway] hot reload failed: {distinct.Length} compile error(s): {string.Join("; ", distinct)}");
        // One message, one chat line per line: separate sends could arrive out of order.
        var lines = new List<string> { $"Hot-reload failed: {distinct.Length} compile error(s), the old code keeps running." };
        lines.AddRange(distinct.Take(ShownErrors));
        if (distinct.Length > ShownErrors) lines.Add($"+{distinct.Length - ShownErrors} more in logs\\live-gateway.err");
        Tell(string.Join('\n', lines));
    }

    // "dotnet watch ❌ C:\...\Program.Commands.cs(1502,10): error CS1525: Invalid expression term ')'" →
    // "Program.Commands.cs(1502,10): CS1525 Invalid expression term ')'".
    static string CompileError(string line)
    {
        int at = line.IndexOf(": error CS", StringComparison.Ordinal);
        if (at < 0) return line;
        int start = line.LastIndexOfAny(['\\', '/'], at) + 1;
        string where = start > 0 ? line[start..at] : line[..at];
        string rest = line[(at + ": error ".Length)..];
        int colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0) rest = $"{rest[..colon]} {rest[(colon + 2)..]}";
        // MSBuild appends the project in brackets.
        int project = rest.LastIndexOf(" [", StringComparison.Ordinal);
        return $"{where}: {(project > 0 && rest.EndsWith(']') ? rest[..project] : rest)}";
    }

    static void OnSourceSaved(string path)
    {
        if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) return;
        long now = Stopwatch.GetTimestamp();
        long since = Interlocked.Read(ref pendingSince);
        if (since != 0 && Stopwatch.GetElapsedTime(since, now) < PendingTimeout) return;
        if (Interlocked.CompareExchange(ref pendingSince, now, since) != since) return;
        Console.WriteLine($"[gateway] hot reload: {Path.GetFileName(path)} saved");
        Tell("Starting Hot-Reload");
    }

    internal static void UpdateApplication(Type[]? updated)
    {
        int n = Interlocked.Increment(ref count);
        long since = Interlocked.Exchange(ref pendingSince, 0);
        string took = since != 0 ? $" in {Stopwatch.GetElapsedTime(since).TotalSeconds:0.00} seconds" : "";
        string types = updated is { Length: > 0 } ? string.Join(", ", updated.Select(t => t.Name).Distinct().Take(8)) : "none named";
        Console.WriteLine($"[gateway] hot reload #{n} applied{took} (types: {types})");
        Tell($"Hot-reload is applied{took}.");
    }

    internal static void Tell(string text)
    {
        foreach (var conn in Players.Keys)
        {
            if (conn.Closed) { Players.TryRemove(conn, out _); continue; }
            if (conn.Commands is not { } commands) continue;
            _ = Task.Run(async () =>
            {
                try { await commands.Reply(text); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Players.TryRemove(conn, out _); }
            });
        }
    }
}
