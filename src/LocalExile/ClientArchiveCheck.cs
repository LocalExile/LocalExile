using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("GameServer.Gateway.Tests")]

namespace LocalExile;

/// <summary>Runs the release's own read-only validator; stdout is a bounded, versioned helper result.</summary>
sealed record ClientArchiveCheck(string IndexSha256, int Files, int Bundles)
{
    const string Unavailable = "The bundled client validator could not complete the check. This does not establish that your game files are damaged. Check or update the LocalExile package.";

    public static async Task<ClientArchiveCheck> RunAsync(string folder, CancellationToken stop = default)
    {
        string archive = Path.GetFullPath(Path.Combine(folder, "Content.ggpk"));
        string tool = Path.Combine("app", Platform.Program("PoEDataExtractor"));
        var start = Tools.Info(tool, ["validate", archive, "--json"]);
        start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.UTF8;
        var result = await CaptureAsync(start, stop);
        return Parse(result.Code, result.Output, result.Error);
    }

    internal static ClientArchiveCheck Parse(int code, string output, string error)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.GetProperty("protocol").GetInt32() != 1) throw new FormatException("unknown validator protocol");
            bool ok = root.GetProperty("ok").GetBoolean();
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (ok && code == 0)
            {
                string hash = Text("indexSha256") ?? "";
                int files = root.GetProperty("fileCount").GetInt32(), bundles = root.GetProperty("bundleCount").GetInt32();
                string?[] tables = root.GetProperty("checkedTables").EnumerateArray().Select(v => v.GetString()).ToArray();
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || files <= 0 || bundles <= 0)
                    throw new FormatException("incomplete validator success");
                if (!tables.SequenceEqual(new[] { "data/worldareas.datc64", "data/baseitemtypes.datc64", "data/stats.datc64" }))
                    throw new FormatException("validator did not report the required protocol-1 table checks");
                return new(hash, files, bundles);
            }
            if (ok || code == 0) throw new FormatException("validator exit code disagrees with its result");
            string reason = Text("reason") ?? throw new FormatException("validator did not give a failure reason");
            string message = Text("message") ?? Unavailable;
            bool client = reason is "invalid-index" or "invalid-archive" or "required-data-unreadable" or "access-denied"
                or "client-unreadable" or "missing-client-file";
            if (reason is "invalid-index" or "invalid-archive" or "required-data-unreadable")
                message += $" Keep this copy for now. LocalExile requires standalone PoE {ClientInstall.RequiredVersion}. Opening the official PoE launcher to repair it may update it to an unsupported version.";
            string? details = Text("details");
            if (!string.IsNullOrWhiteSpace(error))
                details = string.IsNullOrWhiteSpace(details) ? error.Trim() : $"{details}\n{error.Trim()}";
            throw new LauncherException(client ? ExitCode.Client : ExitCode.Unexpected, message, reason, details);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new LauncherException(ExitCode.Unexpected, Unavailable, "validation-unavailable",
                $"{e.Message}; helper exit {code}; {error.Trim()}");
        }
    }

    internal static async Task<(int Code, string Output, string Error)> CaptureAsync(ProcessStartInfo start,
        CancellationToken stop, TimeSpan? timeout = null)
    {
        stop.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var end = CancellationTokenSource.CreateLinkedTokenSource(stop, deadline.Token);
        using var process = new Process { StartInfo = start };
        bool started = false;
        int overflow = 0;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("validator did not start");
            started = true;
            if (!OperatingSystem.IsWindows()) EndWithThisProcess.Add(process);
            process.StandardInput.Close();
            var output = ReadAsync(process.StandardOutput);
            var error = ReadAsync(process.StandardError);
            await Task.WhenAll(output, error, process.WaitForExitAsync(end.Token));
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new LauncherException(ExitCode.Unexpected, Unavailable,
                overflow != 0 ? "validation-output-limit" : "validation-timeout",
                overflow != 0 ? "Validator output exceeded 65536 characters on one stream." : "Validator exceeded its 60-second deadline.");
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            throw new LauncherException(ExitCode.Unexpected, Unavailable, "validation-unavailable", e.Message);
        }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
                if (!OperatingSystem.IsWindows()) EndWithThisProcess.Remove(process);
            }
        }

        async Task<string> ReadAsync(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), end.Token)) != 0)
            {
                if (text.Length + count > 65536)
                {
                    Interlocked.Exchange(ref overflow, 1);
                    end.Cancel();
                    throw new OperationCanceledException(end.Token);
                }
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
    }
}
