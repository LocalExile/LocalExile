using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core.Accounts;
using GameServer.Core.Imports;
using GameServer.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>A separate, bounded receiver for the PoB button. It never runs the game server or accepts filesystem paths.</summary>
sealed class PathOfBuildingServer : IAsyncDisposable
{
    public const int MaximumBodyBytes = 3 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    readonly PathOfBuildingServerConfig config;
    readonly LeagueCatalog leagues;
    readonly SemaphoreSlim importing = new(1, 1);
    readonly Lazy<IReadOnlyDictionary<uint, uint[]>> graph;
    WebApplication? application;

    public sealed record Request(string Account, string Code, string? Name = null, string? League = null);
    public string Address => application?.Urls.Single() ?? throw new InvalidOperationException("The receiver has not started.");

    public PathOfBuildingServer(PathOfBuildingServerConfig config)
    {
        this.config = config;
        leagues = new(config.Leagues);
        graph = new(() => PathOfBuildingPassiveGraph.Load(config.ClientGgpk, config.OozWorker, config.NodeExecutable));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (application is not null) throw new InvalidOperationException("The receiver is already running.");
        // Validate the existing database without creating a new file, changing its schema, or migrating saves.
        using (var db = PathOfBuildingServerTokens.OpenReadOnly(config.DatabasePath))
        using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT name FROM accounts LIMIT 1";
            check.ExecuteScalar();
        }
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = config.RepositoryRoot });
        builder.Logging.ClearProviders(); // Never log bearer credentials, build contents, or request URLs/query strings.
        builder.WebHost.UseUrls(config.ListenUrl);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = MaximumBodyBytes;
            server.Limits.MaxConcurrentConnections = 16;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.AddServerHeader = false;
            if (config.CertificatePath is { } certificate)
                server.ConfigureHttpsDefaults(https => https.ServerCertificate =
                    System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certificate,
                        config.CertificatePasswordEnvironment is { } variable ? Environment.GetEnvironmentVariable(variable) : null));
        });
        var app = builder.Build();
        app.Run(HandleAsync);
        try { await app.StartAsync(cancellationToken); application = app; }
        catch { await app.DisposeAsync(); throw; }
    }

    public Task WaitForShutdownAsync() => application?.WaitForShutdownAsync()
        ?? throw new InvalidOperationException("The receiver has not started.");

    async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        bool create = context.Request.Path == "/api/pob/characters";
        if (!create && context.Request.Path != "/api/pob/preview")
        { await Fail(context, 404, "Unknown PoB API endpoint."); return; }
        if (!HttpMethods.IsPost(context.Request.Method))
        { context.Response.Headers.Allow = "POST"; await Fail(context, 405, "Use POST for this endpoint."); return; }
        if (context.Request.QueryString.HasValue)
        { await Fail(context, 400, "Send fields in the JSON body, not in the URL."); return; }
        if (!context.Request.HasJsonContentType())
        { await Fail(context, 415, "Content-Type must be application/json."); return; }
        if (context.Request.ContentLength > MaximumBodyBytes)
        { await Fail(context, 413, "The build request exceeds the 3 MiB limit."); return; }
        // Reject missing/oversized credentials before reading a body. Account binding is checked after bounded JSON read.
        string authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.Length != 71 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        { await Fail(context, 401, "The account or PoB token is invalid."); return; }
        try
        {
            var request = await ReadRequest(context);
            var account = PathOfBuildingServerTokens.Authenticate(config.DatabasePath, request.Account, authorization);
            if (account is null) { await Fail(context, 401, "The account or PoB token is invalid."); return; }
            if (!await importing.WaitAsync(0, context.RequestAborted))
            { await Fail(context, 429, "Another build is being imported. Please try again shortly."); return; }
            try
            {
                var league = leagues.Find(request.League ?? LeagueCatalog.Standard)
                    ?? throw new ArgumentException("The selected league is not offered by this server.");
                if (league.Ruthless) throw new ArgumentException("Ruthless build import is not supported.");
                var build = PathOfBuilding.Parse(request.Code);
                var progress = PathOfBuildingCharacters.Prepare(build, config.DataFolder, () => graph.Value);
                var items = PathOfBuildingItems.Materialize(build, new ItemCatalog(config.DataFolder), config.DataFolder);
                context.RequestAborted.ThrowIfCancellationRequested();
                Character? character = null;
                if (create)
                {
                    if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Enter a new character name.");
                    using var db = new SaveDatabase(config.DatabasePath);
                    character = db.Atomically(() =>
                    {
                        // A deleted/recreated account cannot inherit the identity authenticated earlier in the request.
                        if (new SqliteAccountStore(db).FindAccount(account.Name)?.Id != account.Id)
                            throw new ArgumentException("The account changed while importing. Request a new PoB token.");
                        return PathOfBuildingCharacters.Create(db, account.Name, request.Name, progress,
                            progress.ToSave(items), league.Name, leagues, config.DataFolder);
                    });
                }
                await context.Response.WriteAsJsonAsync(new
                {
                    ok = true, preview = !create, character = character?.Name ?? request.Name,
                    @class = build.Class.ToString(), level = build.Level, league = league.Name,
                    passives = progress.Passives.Count, masteries = progress.MasteryEffects.Count,
                    items = items.Inventories.Values.Sum(p => p.Count),
                    gems = items.Inventories.Values.SelectMany(p => p).Sum(p => p.Item.Sockets?.Count(s => s.Gem is not null) ?? 0),
                    warnings = progress.Warnings.Concat(items.Warnings).Distinct().ToArray(),
                    next = create ? "Log out to the login screen and log in again, then select this character and Play."
                        : "Preview passed. Forge Character will save a new character under this account."
                }, context.RequestAborted);
            }
            finally { importing.Release(); }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (e.StatusCode == 413)
        { await Fail(context, 413, "The build request exceeds the 3 MiB limit."); }
        catch (RequestTooLargeException) { await Fail(context, 413, "The build request exceeds the 3 MiB limit."); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { await Fail(context, 408, "The build request timed out."); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is FormatException or ArgumentException or JsonException or OverflowException)
        { await Fail(context, 400, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
            or KeyNotFoundException or InvalidOperationException)
        { await Fail(context, 500, "The server could not read its configured game data or save the character. Ask the server owner to check the configuration and database."); }
    }

    static async Task<Request> ReadRequest(HttpContext context)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (body.Length + count > MaximumBodyBytes) throw new RequestTooLargeException();
            body.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("The request must be a JSON object.");
        var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in json.RootElement.EnumerateObject())
            if (!properties.Add(property.Name)) throw new ArgumentException("Duplicate JSON fields are not accepted.");
        var request = json.RootElement.Deserialize<Request>(Json) ?? throw new ArgumentException("The request is empty.");
        if (string.IsNullOrWhiteSpace(request.Account) || request.Account.Length > 32)
            throw new ArgumentException("Enter the local account name (up to 32 characters).");
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > PathOfBuilding.MaximumCodeCharacters)
            throw new ArgumentException("Provide a PoB export code within the 2 MiB limit.");
        if (request.Name?.Length > 23 || request.League?.Length > 128)
            throw new ArgumentException("The character or league name is too long.");
        return request;
    }

    static async Task Fail(HttpContext context, int status, string message)
    {
        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = message.Length <= 4096 ? message : message[..4096] }, context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is { } app) { await app.StopAsync(); await app.DisposeAsync(); application = null; }
        importing.Dispose();
    }

    sealed class RequestTooLargeException : Exception;
}
