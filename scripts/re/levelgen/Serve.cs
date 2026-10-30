using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GameServer.Data.Terrain.Generation;

namespace LevelGenTool;

/// <summary>`levelgen serve`: one process keeps the game files and the terrain cache open behind a named pipe, so a
/// command costs the work it does and not the GGPK's opening and the files' parsing (about 1.5 s per call, more for
/// commands that read many files). Any other command first tries the server (<see cref="Forward"/>): it starts one
/// when there is none, and a server of another build (levelgen or GameServer.Data rebuilt) is told so, exits, and is
/// replaced. The server runs from a copy of the build output (bin/serve/&lt;build&gt;), so builds are not locked.
/// LEVELGEN_LOCAL=1 runs a command here without the server.</summary>
static class Serve
{
    const string Done = "\u0001exit ", Stale = "\u0001stale";
    static readonly TimeSpan Idle = TimeSpan.FromHours(2);

    // One server per checkout (string.GetHashCode differs per process, so a stable hash of the root).
    static string PipeName => "localexile-levelgen-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(Shared.Root().ToLowerInvariant())))[..12];

    /// <summary>This build: the module version ids of levelgen and GameServer.Data (new on every rebuild that changes them).</summary>
    static string Build => $"{typeof(Serve).Assembly.ManifestModule.ModuleVersionId:N}-{typeof(LevelGenerator).Assembly.ManifestModule.ModuleVersionId:N}";

    sealed record Request(string Build, string Cwd, Dictionary<string, string> Env, string[] Args);

    public static int Run(Func<string[], int> dispatch)
    {
        NamedPipeServerStream NewPipe() => new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var log = new StreamWriter(Path.Combine(Shared.Root(), "scripts", "re", "levelgen", "bin", "serve", "serve.log"), append: true) { AutoFlush = true };
        Console.SetError(log);
        NamedPipeServerStream pipe;
        try { pipe = NewPipe(); }
        catch (IOException) { Console.Error.WriteLine("levelgen serve: a server is already running"); return 1; }
        // Warm up: the game files and the cache everything shares.
        Shared.Files();
        Console.Error.WriteLine($"{DateTime.Now:s} levelgen serve: ready ({Build}) on {PipeName}");
        var stdout = Console.Out;
        var stderr = Console.Error;
        while (true)
        {
            var wait = pipe.WaitForConnectionAsync();
            if (!wait.Wait(Idle)) return 0;
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = false };
                var request = JsonSerializer.Deserialize<Request>(reader.ReadLine() ?? "null");
                if (request is null) continue;
                if (request.Build != Build)
                {
                    writer.WriteLine(Stale);
                    writer.Flush();
                    return 0;
                }
                // The caller's environment for the LEVELGEN_* switches, and its directory for relative paths.
                foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("LEVELGEN_", StringComparison.Ordinal)).ToList())
                    Environment.SetEnvironmentVariable(key, null);
                foreach (var (k, v) in request.Env) Environment.SetEnvironmentVariable(k, v);
                Directory.SetCurrentDirectory(request.Cwd);
                Console.SetOut(writer);
                Console.SetError(writer);
                int code;
                try { code = dispatch(request.Args); }
                catch (Exception e) { writer.WriteLine($"{e.GetType().Name}: {e.Message}\n{e.StackTrace}"); code = 1; }
                finally { Console.SetOut(stdout); Console.SetError(stderr); }
                writer.WriteLine(Done + code);
                writer.Flush();
            }
            catch (IOException) { /* the caller went away */ }
            finally
            {
                pipe.Dispose();
                pipe = NewPipe();
            }
        }
    }

    /// <summary>Runs <paramref name="args"/> on the server and returns its exit code; null when there is no server of
    /// this build to use (the caller then runs the command itself).</summary>
    public static int? Forward(string[] args)
    {
        if (args.Length == 0 || Environment.GetEnvironmentVariable("LEVELGEN_LOCAL") is { Length: > 0 }) return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var pipe = Connect(TimeSpan.FromMilliseconds(300));
            if (pipe is null)
            {
                if (!Start()) return null;
                pipe = Connect(TimeSpan.FromSeconds(60));
                if (pipe is null) return null;
            }
            using (pipe)
            {
                var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                    .Where(e => ((string)e.Key).StartsWith("LEVELGEN_", StringComparison.Ordinal))
                    .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
                writer.WriteLine(JsonSerializer.Serialize(new Request(Build, Directory.GetCurrentDirectory(), env, args)));
                writer.Flush();
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
                for (string? line; (line = reader.ReadLine()) is not null;)
                {
                    if (line == Stale) break;
                    if (line.StartsWith(Done, StringComparison.Ordinal)) return int.Parse(line[Done.Length..]);
                    Console.WriteLine(line);
                }
                // A server of another build: it has exited; start this build's on the next attempt.
                Thread.Sleep(200);
            }
        }
        return null;
    }

    static NamedPipeClientStream? Connect(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        try { pipe.Connect(timeout); return pipe; }
        catch (Exception e) when (e is TimeoutException or IOException) { pipe.Dispose(); return null; }
    }

    /// <summary>Starts a server of this build from a copy of the build output, detached from this process.</summary>
    static bool Start()
    {
        try
        {
            string from = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string serveRoot = Path.Combine(Shared.Root(), "scripts", "re", "levelgen", "bin", "serve");
            string to = Path.Combine(serveRoot, Build);
            if (!File.Exists(Path.Combine(to, "levelgen.dll")))
            {
                // Older copies go (those still running stay locked and are skipped).
                if (Directory.Exists(serveRoot))
                    foreach (var old in Directory.GetDirectories(serveRoot))
                        try { Directory.Delete(old, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                string temp = to + ".tmp";
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(temp, Path.GetRelativePath(from, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                Directory.Move(temp, to);
            }
            // Through the shell, hidden: the server inherits none of this process's handles (a caller waiting for its
            // output to close would wait for the server too) and outlives it. It logs to bin/serve/serve.log.
            var psi = new ProcessStartInfo("dotnet", $"\"{Path.Combine(to, "levelgen.dll")}\" serve")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Shared.Root(),
            };
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"levelgen: could not start the server ({e.Message}); running here");
            return false;
        }
    }
}
