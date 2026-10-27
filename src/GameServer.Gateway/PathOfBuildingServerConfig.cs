using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Administrator-owned configuration. Clients may submit builds, never server filesystem paths.</summary>
sealed record PathOfBuildingServerConfig
{
    public string ListenUrl { get; init; } = "http://127.0.0.1:20496";
    public string RepositoryRoot { get; init; } = "..";
    public string DataFolder { get; init; } = "data";
    public string DatabasePath { get; init; } = "data/saves.db";
    public string ClientGgpk { get; init; } = "Client/Content.ggpk";
    public string OozWorker { get; init; } = "Tools/ooz/ooz_worker.mjs";
    public string NodeExecutable { get; init; } = "node";
    public string[] Leagues { get; init; } = ["Standard"];
    public string? CertificatePath { get; init; }
    public string? CertificatePasswordEnvironment { get; init; }

    public static PathOfBuildingServerConfig Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<PathOfBuildingServerConfig>(File.ReadAllBytes(fullPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new ArgumentException("The PoB server configuration must be a JSON object.");
        if (!Uri.TryCreate(config.ListenUrl, UriKind.Absolute, out var listen)
            || listen.Scheme is not ("http" or "https") || listen.UserInfo.Length != 0
            || listen.AbsolutePath != "/" || listen.Query.Length != 0 || listen.Fragment.Length != 0)
            throw new ArgumentException("listenUrl must be an HTTP(S) origin, for example http://127.0.0.1:20496.");
        string root = Resolve(Path.GetDirectoryName(fullPath)!, config.RepositoryRoot);
        string node = config.NodeExecutable;
        if (string.IsNullOrWhiteSpace(node)) throw new ArgumentException("nodeExecutable cannot be empty.");
        if (node.Contains('/') || node.Contains('\\')) node = Resolve(root, node);
        if (config.Leagues is null || config.Leagues.Length == 0 || config.Leagues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("leagues must contain at least one nonempty local league name.");
        string? certificate = config.CertificatePath is null ? null : Resolve(root, config.CertificatePath);
        if (listen.Scheme == "https" && certificate is null)
            throw new ArgumentException("Direct HTTPS requires certificatePath. A TLS reverse proxy can instead forward to a loopback HTTP listener.");
        return config with { ListenUrl = listen.GetLeftPart(UriPartial.Authority), RepositoryRoot = root,
            DataFolder = Resolve(root, config.DataFolder), DatabasePath = Resolve(root, config.DatabasePath),
            ClientGgpk = Resolve(root, config.ClientGgpk), OozWorker = Resolve(root, config.OozWorker),
            NodeExecutable = node, CertificatePath = certificate };
    }

    static string Resolve(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Configured folders and file paths cannot be empty.");
        return Path.GetFullPath(path, root);
    }
}
