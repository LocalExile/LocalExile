using System.Text;
using System.Text.Json;

namespace LocalExile;

/// <summary>What the launcher tells the player, or with --json an external program: one JSON object per line on standard
/// output, with "schema", "event" and "time" always. Events: started, step, progress, info, warning, value, needs-input,
/// ready, done, error (Agents/docs/dev/releases.md). A change that breaks a reader raises <see cref="Schema"/>.</summary>
sealed class Output
{
    const int Schema = 1;
    readonly object _lock = new();

    public bool Json { get; }

    public Output(bool json)
    {
        Json = json;
        Console.OutputEncoding = new UTF8Encoding(false);
    }

    public void Started(string version)
    {
        if (Json) Event("started", new() { ["version"] = version });
        else Line($"LocalExile {version}");
    }

    /// <summary>A new part of the work begins.</summary>
    public void Step(string id, string message)
    {
        if (Json) Event("step", new() { ["id"] = id, ["message"] = message });
        else Line(message);
    }

    /// <summary>A long part is still working; current and total when it can count.</summary>
    public void Progress(string id, string message, long? current = null, long? total = null)
    {
        if (Json) Event("progress", new() { ["id"] = id, ["message"] = message, ["current"] = current, ["total"] = total });
        else Line("  " + message);
    }

    public void Info(string message)
    {
        if (Json) Event("info", new() { ["message"] = message });
        else Line("  " + message);
    }

    public void Warn(string message)
    {
        if (Json) Event("warning", new() { ["message"] = message });
        else Line("  warning: " + message);
    }

    public void Value(string name, string value)
    {
        if (Json) Event("value", new() { ["name"] = name, ["value"] = value });
        else Console.WriteLine(value);
    }

    /// <summary>One client build LocalExile runs (the builds command). Steam's ids are text: a manifest id is larger than
    /// a JSON reader's exact numbers.</summary>
    public void Build(GameServer.Core.Protocol.ClientBuild build)
    {
        if (Json)
        {
            Event("build", new()
            {
                ["version"] = build.Version, ["name"] = build.Name, ["executableSha256"] = build.ExecutableSha256,
                ["steamIndexSha256"] = build.SteamIndexSha256,
                ["steamApp"] = build.SteamManifests is null ? null : GameServer.Core.Protocol.ClientBuilds.SteamApp.ToString(),
                ["steamManifests"] = build.SteamManifests?.Select(m => new Dictionary<string, string>
                    { ["depot"] = m.Depot.ToString(), ["manifest"] = m.Manifest.ToString() }).ToList(),
            });
            return;
        }
        Console.WriteLine(build);
        Console.WriteLine($"  executable SHA-256  {build.ExecutableSha256}");
        Console.WriteLine($"  Steam install       {(build.SteamIndexSha256 is null ? "not accepted" : "accepted")}");
        if (build.SteamManifests is { } manifests)
            Console.WriteLine($"  Steam download      app {GameServer.Core.Protocol.ClientBuilds.SteamApp}: {string.Join(", ", manifests.Select(m => $"depot {m.Depot} manifest {m.Manifest}"))}");
    }

    /// <summary>--json never asks: the program runs again with the option this names.</summary>
    public LauncherException NeedsInput(string option, string message, string? suggestion = null)
    {
        if (Json) Event("needs-input", new() { ["option"] = option, ["message"] = message, ["suggestion"] = suggestion });
        return new LauncherException(ExitCode.NeedsInput, message);
    }

    public void Ready(Ports ports, int? clientPid)
    {
        if (Json)
        {
            Event("ready", new() { ["clientPid"] = clientPid, ["patch"] = ports.Patch, ["login"] = ports.Login, ["gateway"] = ports.Gateway });
            return;
        }
        if (clientPid is null)
        {
            Line($"Servers ready (login 127.0.0.1:{ports.Login}, game 127.0.0.1:{ports.Gateway}). Ctrl+C stops them.");
            return;
        }
        Line("The client is starting; it loads for about a minute. Closing it, or this window, stops the servers.");
        Line("Log in with your LocalExile account: its name goes in the Email field.");
    }

    public void Done(string message)
    {
        if (Json) Event("done", new() { ["message"] = message });
        else Line(message);
    }

    public void Fail(ExitCode code, string message)
    {
        if (Json) Event("error", new() { ["code"] = (int)code, ["kind"] = code.ToString(), ["message"] = message });
        else Line($"ERROR: {message}");
    }

    void Line(string text)
    {
        lock (_lock) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
    }

    void Event(string name, Dictionary<string, object?> fields)
    {
        var all = new Dictionary<string, object?> { ["schema"] = Schema, ["event"] = name, ["time"] = DateTimeOffset.UtcNow.ToString("O") };
        foreach (var (key, value) in fields) if (value is not null) all[key] = value;
        string line = JsonSerializer.Serialize(all);
        lock (_lock) Console.WriteLine(line);
    }
}
