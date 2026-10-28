using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core.Accounts;
using GameServer.Storage;

/// <summary>Only listener policy is separate. Storage, client and leagues follow normal server startup.</summary>
sealed record PathOfBuildingServerConfig
{
    public const string DefaultPath = "config/pob-server.json";
    public bool Enabled { get; init; }
    public string ListenUrl { get; init; } = "http://127.0.0.1:20496";
    public string[]? AllowedLeagues { get; init; }
    public string? CertificatePath { get; init; }
    public string? CertificatePasswordEnvironment { get; init; }

    // Internal overrides allow isolated test fixtures. JSON cannot select another database or game-data tree.
    internal string RepositoryRoot { get; init; } = Directory.GetCurrentDirectory();
    internal string DataFolder { get; init; } = "data";
    internal string DatabasePath { get; init; } = SaveDatabase.DefaultPath;
    internal string ClientGgpk { get; init; } = Environment.GetEnvironmentVariable("Game_GGPK")
        ?? Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("Game_CLIENT_EXE") ?? "Client/Game.exe")!, "Content.ggpk");
    internal string OozWorker { get; init; } = "Tools/ooz/ooz_worker.mjs";
    internal string NodeExecutable { get; init; } = "node";
    internal LeagueCatalog Leagues { get; init; } = LeagueCatalog.FromEnvironment();

    public static PathOfBuildingServerConfig Load(string path, bool optional = false)
    {
        var config = optional && !File.Exists(path) ? new() :
            JsonSerializer.Deserialize<PathOfBuildingServerConfig>(File.ReadAllBytes(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new ArgumentException("The PoB server configuration must be a JSON object.");
        return config.Validate();
    }

    internal PathOfBuildingServerConfig Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var listen)
            || listen.Scheme is not ("http" or "https") || listen.UserInfo.Length != 0
            || listen.AbsolutePath != "/" || listen.Query.Length != 0 || listen.Fragment.Length != 0)
            throw new ArgumentException("listenUrl must be an HTTP(S) origin, for example http://127.0.0.1:20496.");
        if (AllowedLeagues is { } allowed && (allowed.Length == 0
            || allowed.Any(n => string.IsNullOrWhiteSpace(n) || Leagues.Find(n) is null)))
            throw new ArgumentException("allowedLeagues must list full league names offered by Game_LEAGUES.");
        if (listen.Scheme == "https" && string.IsNullOrWhiteSpace(CertificatePath))
            throw new ArgumentException("Direct HTTPS requires certificatePath. A TLS reverse proxy can instead forward to loopback HTTP.");
        return this with { ListenUrl = listen.GetLeftPart(UriPartial.Authority),
            CertificatePath = CertificatePath is null ? null : Path.GetFullPath(CertificatePath, RepositoryRoot) };
    }

    internal League SelectLeague(string name)
    {
        var league = Leagues.Find(name) ?? throw new ArgumentException("The selected league is not offered by this server.");
        if (AllowedLeagues is { } allowed && !allowed.Contains(league.Name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("PoB import is disabled for the selected league.");
        if (league.Ruthless) throw new ArgumentException("Ruthless build import is not supported.");
        return league;
    }
}
