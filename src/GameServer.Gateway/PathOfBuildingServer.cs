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

/// <summary>A bounded HTTP listener hosted by Gateway (or explicitly started alone for administration).</summary>
sealed class PathOfBuildingServer : IAsyncDisposable
{
    public const int MaximumBodyBytes = 3 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    readonly PathOfBuildingServerConfig config;
    readonly TextWriter diagnostics;
    readonly SemaphoreSlim importing = new(1, 1);
    readonly Lazy<IReadOnlyDictionary<uint, uint[]>> graph;
    WebApplication? application;

    public sealed record Request(string Account, string Code, string? Name = null, string? League = null);
    public string Address => application?.Urls.Single() ?? throw new InvalidOperationException("The receiver has not started.");

    public PathOfBuildingServer(PathOfBuildingServerConfig config, TextWriter? diagnostics = null)
    {
        this.config = config.Validate();
        this.diagnostics = TextWriter.Synchronized(diagnostics ?? Console.Error);
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

    public CancellationToken Stopping => application?.Lifetime.ApplicationStopping ?? CancellationToken.None;

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
        string phase = "authenticate";
        try
        {
            var request = await ReadRequest(context);
            var account = PathOfBuildingServerTokens.Authenticate(config.DatabasePath, request.Account, authorization);
            if (account is null) { await Fail(context, 401, "The account or PoB token is invalid."); return; }
            if (!await importing.WaitAsync(0, context.RequestAborted))
            { await Fail(context, 429, "Another build is being imported. Please try again shortly."); return; }
            try
            {
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, Stopping);
                var league = config.SelectLeague(request.League ?? LeagueCatalog.Standard);
                if (create && string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Enter a new character name.");
                ValidateDestination(account, request.Name);
                phase = "prepare";
                // Extraction/catalog preparation is bounded by the export limits and runs outside the gameplay loop.
                var prepared = await Task.Run(() => PathOfBuildingImport.Prepare(request.Code, config, () => graph.Value), stopping.Token);
                var progress = prepared.Progress;
                var items = prepared.Equipment;
                phase = "serialize";
                byte[] response = JsonSerializer.SerializeToUtf8Bytes(prepared.Response(!create, request.Name, league.Name));
                if (response.Length > 64 * 1024) throw new InvalidOperationException("PoB response exceeded its server bound.");
                stopping.Token.ThrowIfCancellationRequested();
                if (create)
                {
                    phase = "save";
                    using var db = new SaveDatabase(config.DatabasePath);
                    db.Atomically(() =>
                    {
                        // A deleted/recreated account cannot inherit the identity authenticated earlier in the request.
                        if (new SqliteAccountStore(db).FindAccount(account.Name)?.Id != account.Id)
                            throw new ArgumentException("The account changed while importing. Request a new PoB token.");
                        return PathOfBuildingCharacters.Create(db, account.Name, request.Name!, progress,
                            progress.ToSave(items), league.Name, config.Leagues, config.DataFolder);
                    });
                }
                phase = create ? "send-created-response" : "send-preview-response";
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = response.Length;
                await context.Response.Body.WriteAsync(response, context.RequestAborted);
            }
            finally { importing.Release(); }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (e.StatusCode == 413)
        { await Fail(context, 413, "The build request exceeds the 3 MiB limit."); }
        catch (RequestTooLargeException) { await Fail(context, 413, "The build request exceeds the 3 MiB limit."); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested && !Stopping.IsCancellationRequested)
        { await Fail(context, 408, "The build request timed out."); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is FormatException or ArgumentException or JsonException or OverflowException)
        { await Fail(context, 400, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
            or KeyNotFoundException or InvalidOperationException)
        {
            string id = Guid.NewGuid().ToString("N");
            // Exception messages can contain submitted XML/mod text, credentials or paths. Log structural diagnostics
            // only, with a reference that the owner can match to the client error.
            string code = e is Microsoft.Data.Sqlite.SqliteException sql ? $" sqlite={sql.SqliteErrorCode}/{sql.SqliteExtendedErrorCode}" : "";
            var frames = new System.Diagnostics.StackTrace(e).GetFrames();
            string stack = string.Join(" <- ", frames.Take(8).Select(f => f.GetMethod()).OfType<System.Reflection.MethodBase>()
                .Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));
            diagnostics.WriteLine($"[pob] failure {id} phase={phase} type={e.GetType().Name} hresult={e.HResult}{code} at {stack}");
            await Fail(context, 500, $"The server could not prepare or save this build. Ask the server owner to check the Gateway log (reference {id}).");
        }
    }

    void ValidateDestination(Account account, string? name)
    {
        if (!string.IsNullOrEmpty(name) && !AccountService.IsValidCharacterName(name))
            throw new ArgumentException("Choose a new character name: 3-23 letters, digits, underscores or hyphens.");
        using var db = PathOfBuildingServerTokens.OpenReadOnly(config.DatabasePath);
        using var check = db.CreateCommand();
        check.CommandText = "SELECT count(*) FROM account_characters WHERE account_id=$account";
        check.Parameters.AddWithValue("$account", account.Id.ToString());
        if (Convert.ToInt32(check.ExecuteScalar()) >= AccountService.DefaultMaxCharacters)
            throw new ArgumentException("This account has reached its character limit. Free a slot before importing.");
        if (string.IsNullOrEmpty(name)) return;
        check.CommandText = "SELECT 1 FROM account_characters WHERE name=$name UNION ALL SELECT 1 FROM characters WHERE name=$name LIMIT 1";
        check.Parameters.AddWithValue("$name", name);
        if (check.ExecuteScalar() is not null) throw new ArgumentException("That character name or save already exists. Choose a new name.");
        PathOfBuildingCharacters.RequireNoLegacySave(config.DatabasePath, name, config.DataFolder);
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
