using System.Runtime.CompilerServices;
using System.Text.Json;

namespace GameServer.Gateway.Tests;

static class TestEnvironment
{
    internal static string RepositoryRoot { get; private set; } = null!;
    internal static string RuntimeRoot { get; private set; } = null!;

    // Real dispatch tests use Program's production catalogs, relative to the selected runtime directory.
    // Set this once before parallel tests or lazy catalogs initialize, rather than changing process CWD in a test.
    [ModuleInitializer]
    internal static void Initialize()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(SourceFile())! })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))
                    && File.Exists(Path.Combine(dir.FullName, "src", "GameServer.Gateway", "GameServer.Gateway.csproj"))
                    && Directory.Exists(Path.Combine(dir.FullName, "data", "schema")))
                {
                    RepositoryRoot = dir.FullName;
                    string? runtime = Environment.GetEnvironmentVariable("Game_RUNTIME_DIR");
                    string config = Path.Combine(RepositoryRoot, "client.local.json");
                    if (string.IsNullOrEmpty(runtime) && File.Exists(config))
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(config));
                        if (json.RootElement.TryGetProperty("runtimeDirectory", out var value))
                            runtime = value.GetString();
                    }
                    if (runtime is not null && string.IsNullOrWhiteSpace(runtime))
                        throw new InvalidDataException("runtimeDirectory must not be whitespace.");
                    RuntimeRoot = Path.GetFullPath(runtime ?? ".", RepositoryRoot);
                    if (!Directory.Exists(Path.Combine(RuntimeRoot, "data", "tables")))
                        throw new DirectoryNotFoundException("Gateway tests need data/tables in the configured runtime.");
                    Directory.SetCurrentDirectory(RuntimeRoot);
                    return;
                }
        throw new DirectoryNotFoundException("Gateway tests need a LocalExile checkout with data/tables.");
    }

    // Isolated --artifacts-path output can be a sibling of the checkout, with no repository among its ancestors.
    static string SourceFile([CallerFilePath] string path = "") => path;
}
