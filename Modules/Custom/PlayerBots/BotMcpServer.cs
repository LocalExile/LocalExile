using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameServer.Modules.PlayerBots;

sealed class BotMcpServer : IDisposable
{
    internal const string KeyFile = "data/modules/PlayerBots-mcp.key";
    readonly HttpListener listener = new();
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim slots = new(4);
    readonly ConcurrentDictionary<HttpListenerContext, byte> active = new();
    readonly BotMcpProtocol protocol;
    readonly byte[] key;
    readonly Action<string> log;
    readonly Task pump;
    int disposed;

    internal BotMcpServer(BotDirector director, int port, Action<string> log)
        : this(new BotMcpProtocol(director.McpCall, director.McpConfiguration), port, log) { }

    internal BotMcpServer(BotMcpProtocol protocol, int port, Action<string> log)
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("MCP port must be 1024..65535.");
        this.log = log;
        this.protocol = protocol;
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        if (!File.Exists(KeyFile))
        {
            try
            {
                using var file = new FileStream(KeyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] value = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                file.Write(value);
            }
            catch (IOException) when (File.Exists(KeyFile)) { }
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string token = File.ReadAllText(KeyFile).Trim();
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid MCP key file.");
        key = Encoding.ASCII.GetBytes(token);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        pump = Task.Run(Pump);
        log($"bots MCP: http://127.0.0.1:{port}/mcp/ (key in {KeyFile}; never log/share the key)");
    }

    async Task Pump()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { break; }
            if (!await slots.WaitAsync(0, stop.Token)) { context.Response.StatusCode = 429; context.Response.Close(); continue; }
            active.TryAdd(context, 0);
            _ = Handle(context);
        }
    }

    async Task Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            if (request.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { response.StatusCode = 404; return; }
            if (request.Headers["Origin"] is not null) { response.StatusCode = 403; return; }
            string? authorization = request.Headers["Authorization"];
            if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(key, Encoding.ASCII.GetBytes(authorization[7..])))
            { response.StatusCode = 401; return; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; response.Headers["Allow"] = "POST"; return; }
            if (request.ContentType?.Split(';')[0].Trim() != "application/json") { response.StatusCode = 415; return; }
            if (request.ContentLength64 > 65536) { response.StatusCode = 413; return; }
            using var body = new MemoryStream();
            byte[] chunk = new byte[4096];
            int count;
            while ((count = await request.InputStream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (body.Length + count > 65536) { response.StatusCode = 413; return; }
                body.Write(chunk, 0, count);
            }
            object? result;
            try
            {
                using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
                bool mismatch = json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && ((version.GetString() == BotMcpProtocol.Modern && request.Headers["MCP-Protocol-Version"] != BotMcpProtocol.Modern)
                        || request.Headers["MCP-Protocol-Version"] is { } supplied && supplied != version.GetString());
                if (mismatch)
                {
                    response.StatusCode = 400;
                    object? id = json.RootElement.TryGetProperty("id", out var value) ? value.Clone() : null;
                    result = BotMcpProtocol.Error(id, -32600, "HTTP protocol header must match request metadata.");
                }
                else result = protocol.Reply(json.RootElement, request.Headers["MCP-Protocol-Version"]);
            }
            catch (JsonException) { result = BotMcpProtocol.Error(null, -32700, "Invalid JSON."); }
            if (result is null) { response.StatusCode = 202; return; }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            using (var parsed = JsonDocument.Parse(bytes))
                if (parsed.RootElement.TryGetProperty("error", out var error) && error.GetProperty("code").GetInt32() == -32022)
                    response.StatusCode = 400;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, timeout.Token);
        }
        catch (Exception e) when (e is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException)
        { if (!stop.IsCancellationRequested && e is not OperationCanceledException) log("bots MCP: request disconnected"); }
        catch (Exception e)
        {
            log($"bots MCP: request failed ({e.GetType().Name})");
            try { response.StatusCode = 500; } catch (ObjectDisposedException) { }
        }
        finally
        {
            try { response.Close(); } catch (ObjectDisposedException) { }
            active.TryRemove(context, out _);
            slots.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        listener.Close();
        foreach (var context in active.Keys)
            try { context.Response.Abort(); } catch (ObjectDisposedException) { }
        try { pump.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        log("bots MCP: stopped; external leases are released by the module");
    }
}
