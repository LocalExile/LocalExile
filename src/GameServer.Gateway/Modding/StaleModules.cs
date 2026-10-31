using System.Diagnostics;

/// <summary>Modules are compiled against the Gateway, and `dotnet watch` rebuilds only the Gateway when an edit needs a
/// restart: an older module would load and fail only when it runs, with a MissingFieldException or MissingMethodException
/// on a member the edit removed. So a watched start (DOTNET_WATCH=1) rebuilds the modules older than the server, before
/// any is loaded. The compiler leaves a module as it is when the Gateway's API did not change, so its date stays old:
/// the checked file (written here and by live.ps1) records the last time they all built against the server. Mods use
/// only GameServer.Modding, and the Gateway compiles leagues itself.</summary>
static class StaleModules
{
    const int Concurrent = 4;
    static readonly string[] ServerAssemblies = ["GameServer.Gateway.dll", "GameServer.Core.dll", "GameServer.Modding.dll"];
    static string CheckedFile => Path.Combine(AppContext.BaseDirectory, "modules-checked");

    internal static void Rebuild(string folder)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_WATCH") != "1" || !Directory.Exists(folder)) return;
        DateTime server = ServerAssemblies.Max(name => File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, name)));
        // A missing file reads as 1601, older than any server.
        if (File.GetLastWriteTimeUtc(CheckedFile) >= server) return;
        string[] projects = [.. Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories)];
        if (!projects.Any(p => File.GetLastWriteTimeUtc(Binary(p)) < server)) return;
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"[modules] older than the Gateway: building {projects.Length} module(s)");
        // The others are compiled against Game, so it goes first.
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        void Build(string project) { if (!BuildProject(project)) failed.Add(Path.GetFileNameWithoutExtension(project)); }
        foreach (string game in projects.Where(IsGame)) Build(game);
        Parallel.ForEach(projects.Where(p => !IsGame(p)), new ParallelOptions { MaxDegreeOfParallelism = Concurrent }, Build);
        if (failed.IsEmpty) File.WriteAllText(CheckedFile, DateTime.UtcNow.ToString("O"));
        Console.WriteLine(failed.IsEmpty
            ? $"[modules] built in {clock.Elapsed.TotalSeconds:0.0} s"
            : $"[modules] did not build, their old builds may fail when they run: {string.Join(", ", failed)}");
    }

    static bool IsGame(string project) => Path.GetFileNameWithoutExtension(project) == "Game";

    // Modules/Directory.Build.props: <folder>/bin/<lowercase project name>.dll.
    static string Binary(string project) => Path.Combine(Path.GetDirectoryName(project)!, "bin",
        Path.GetFileNameWithoutExtension(project).ToLowerInvariant() + ".dll");

    static bool BuildProject(string project)
    {
        // BuildProjectReferences=false: the running Gateway holds its own bin\, which `dotnet watch` has just built.
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (string arg in new[] { "build", project, "--no-restore", "-v", "q", "-nologo", "-nr:false", "-p:BuildProjectReferences=false" })
            start.ArgumentList.Add(arg);
        // What `dotnet watch` sets for the Gateway is not for the build: its hot reload agent would load into the SDK too.
        foreach (string name in start.Environment.Keys.Where(k => k.StartsWith("DOTNET_WATCH", StringComparison.OrdinalIgnoreCase)
            || k is "DOTNET_STARTUP_HOOKS" or "DOTNET_MODIFIABLE_ASSEMBLIES").ToArray())
            start.Environment.Remove(name);
        try
        {
            using var build = Process.Start(start);
            if (build is null) return false;
            build.WaitForExit();
            return build.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
