using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GameServer.Core.Protocol.Patch;
using GameServer.Data.Ggpk;

// GameServer.Patch — patch CDN front-end.
//
// Serves gateway_list.txt ourselves (to point the login host at GameServer.Auth) and
// answers 404 for everything else, so a client that decides to patch can never download
// real files over the patched client. Only with the explicit opt-in Game_CDN_PASSTHROUGH=1
// (or --cdn-passthrough) does it pass other requests through to the real CDN
// (Game_CDN_MIRROR), forwarding the client's Basic auth, to let stale local data repair itself.
//
// It also runs the patching server's TCP protocol (Agents/docs/re/010): start the client with
// `--patching-server 127.0.0.1:12995` and it is told its local data is current, so it
// skips patching and goes straight to the login screen. The welcome's web roots are always
// this server (default http://127.0.0.1:8080/, Game_PATCH_WEB_ROOT to override), never the CDN.
// The hash comes from data/client-constants.json: the server never opens the client's files.
// Env: Game_PATCH_TCP_PORT (default 12995).
//
// Tools (run while the client is closed; these do open the GGPK):
//   root-hash [ggpk] [--save]             print the GGPK root hash; --save also writes it to data/client-constants.json
//   gateway [ggpk]                        print the gateway list the client will use
//   gateway [ggpk] --login <host:port>    point every gateway at <host:port>; the original
//                                         is saved once to data/gateway_list.original.txt
//   gateway [ggpk] --restore              write the saved original back

if (args.Length > 0 && args[0] is "root-hash" or "gateway")
{
    try { return GgpkTool(args); }
    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
    {
        Console.Error.WriteLine($"[patch] GGPK not found ({ex.Message}). Copy the standalone client into Client/ (Client/Content.ggpk) and run from the repository root.");
        return 1;
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"[patch] cannot open the GGPK ({ex.Message}). Close the client first.");
        return 1;
    }
}

GameServer.Core.Diagnostics.ServerLog.Install("patch");
bool passthroughFlag = args.Contains(PatchServerConfig.PassthroughFlag);
args = args.Where(a => a != PatchServerConfig.PassthroughFlag).ToArray();
var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

string? pfx = Environment.GetEnvironmentVariable("Game_TLS_PFX");
bool useTls = pfx is not null && File.Exists(pfx);
string defaultUrl = useTls ? "https://0.0.0.0:443" : PatchServerConfig.DefaultListenUrl;
string listenUrl = Environment.GetEnvironmentVariable("Game_PATCH_URL") ?? defaultUrl;
builder.WebHost.UseUrls(listenUrl);
if (useTls)
{
    var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx!, Environment.GetEnvironmentVariable("Game_TLS_PFX_PASSWORD"));
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = cert));
}

var app = builder.Build();

int patchTcpPort = int.TryParse(Environment.GetEnvironmentVariable("Game_PATCH_TCP_PORT"), out var ptp) ? ptp : 12995;
var config = PatchServerConfig.From(Environment.GetEnvironmentVariable, listenUrl, passthroughFlag);
_ = Task.Run(() => ServePatchProtocol(patchTcpPort, config.WebRoot, config.BackupWebRoot));

string cdnBase = config.CdnBase;
string gatewayList = LoadGatewayList();
byte[] gatewayListBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(gatewayList)).ToArray();
Console.WriteLine(config.CdnPassthrough
    ? $"[patch] CDN PASSTHROUGH ENABLED: requests other than gateway_list.txt go to {cdnBase}"
    : "[patch] cdn passthrough off: only gateway_list.txt is served, everything else is 404");
Console.WriteLine($"[patch] gateway_list.txt -> {gatewayList.TrimEnd()}");

var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(15) })
{
    Timeout = TimeSpan.FromMinutes(10),
};

int served = 0, passthrough = 0, failed = 0;
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
int logged = 0;

app.Run(async ctx =>
{
    var req = ctx.Request;
    string path = req.Path.Value ?? "/";

    if (path.EndsWith("gateway_list.txt", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.Body.WriteAsync(gatewayListBytes);
        served++;
        return;
    }

    if (logged < 30 && seen.Add(path))
    {
        Console.WriteLine($"[patch] {req.Method} {path}{req.QueryString}");
        logged++;
    }

    if (!config.CdnPassthrough)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    string target = cdnBase + path + req.QueryString;
    using var upstream = new HttpRequestMessage(HttpMethod.Get, target);
    if (req.Headers.TryGetValue("Authorization", out var auth))
        upstream.Headers.TryAddWithoutValidation("Authorization", auth.ToString());
    upstream.Headers.TryAddWithoutValidation("Accept", "*/*");

    try
    {
        using var resp = await http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
        if (resp.Content.Headers.ContentType is { } ct) ctx.Response.ContentType = ct.ToString();
        if (resp.Content.Headers.ContentLength is { } cl) ctx.Response.ContentLength = cl;
        await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        passthrough++;
        if (passthrough % 500 == 0)
            Console.WriteLine($"[patch] pass={passthrough} gw={served} fail={failed}");
    }
    catch (Exception ex)
    {
        failed++;
        if (failed <= 20) Console.WriteLine($"[patch] proxy-fail {target}: {ex.GetType().Name}: {ex.Message}");
        if (!ctx.Response.HasStarted) ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
});

Console.WriteLine($"[patch] totals: pass={passthrough} gw={served} fail={failed}");
app.Run();
return 0;

// Answers with the GGPK's root hash, so the client's local data always looks current.
static async Task ServePatchProtocol(int port, string webRoot, string backupWebRoot)
{
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    Console.WriteLine($"[patch] patching protocol on tcp/{port}, web root={webRoot}, backup={backupWebRoot}");
    while (true)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        string peer = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            var stream = tcp.GetStream();
            byte[] buf = new byte[256];
            int have = 0;
            byte? version;
            while ((version = PatchProtocol.TryReadHello(buf.AsSpan(0, have))) is null)
            {
                int n = await stream.ReadAsync(buf.AsMemory(have));
                if (n == 0) throw new IOException("closed before hello");
                have += n;
            }
            // Read per connection: live.ps1 refreshes it before each client start. Without it the connection closes: a
            // wrong hash would make the client start patching.
            byte[] hash = ClientConstants.Load().GgpkRootHashBytes;
            Console.WriteLine($"[patch] {peer} hello v{version}{(version == PatchProtocol.ExpectedVersion ? "" : " (unexpected)")}; root hash {Convert.ToHexString(hash).ToLowerInvariant()}");
            await stream.WriteAsync(PatchProtocol.EncodeWelcome(hash, webRoot, backupWebRoot));
            // A client that still wants to patch sends more requests; log them (not implemented).
            while (true)
            {
                int n = await stream.ReadAsync(buf);
                if (n == 0) break;
                Console.WriteLine($"[patch] {peer} sent {n} more bytes (unhandled): {Convert.ToHexString(buf, 0, Math.Min(n, 64))}");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or System.Text.Json.JsonException or FormatException)
        {
            Console.WriteLine($"[patch] {peer} error: {GameServer.Core.Diagnostics.ServerLog.Describe(ex)}");
        }
        Console.WriteLine($"[patch] {peer} disconnected");
    }
}

static int GgpkTool(string[] args)
{
    var rest = args.Skip(1).ToList();
    string ggpk = rest.Count > 0 && !rest[0].StartsWith("--") ? rest[0] : Find("Client/Content.ggpk") ?? "Client/Content.ggpk";
    if (args[0] == "root-hash")
    {
        Console.WriteLine(Convert.ToHexString(GgpkMaintenance.RootHash(ggpk)).ToLowerInvariant());
        if (rest.Contains("--save")) SaveRootHash(ggpk);
        return 0;
    }
    // The client reads gateway_list.txt, then Bundles2/gateway_list.txt if that exists (0x140cfc2d0); the last one wins.
    string? file = new[] { "Bundles2/gateway_list.txt", "gateway_list.txt" }.FirstOrDefault(f => GgpkMaintenance.TryReadFile(ggpk, f) is not null);
    if (file is null) { Console.Error.WriteLine("[patch] no gateway_list.txt in the GGPK"); return 1; }
    byte[] current = GgpkMaintenance.TryReadFile(ggpk, file)!;
    int at = rest.IndexOf("--login");
    if (at >= 0 && at + 1 < rest.Count)
    {
        string backup = Path.Combine("data", "gateway_list.original.txt");
        Directory.CreateDirectory("data");
        if (!File.Exists(backup)) File.WriteAllBytes(backup, current);
        string text = DecodeList(current);
        string patched = string.Join("\r\n", text.Split("\r\n").Select(line =>
        {
            var f = line.Split(';');
            if (f.Length >= 3) f[2] = rest[at + 1];
            return string.Join(';', f);
        }));
        GgpkMaintenance.ReplaceFile(ggpk, file, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(patched)]);
        SaveRootHash(ggpk);
        Console.WriteLine($"[patch] {file} now points at {rest[at + 1]} (original saved to {backup})");
        current = GgpkMaintenance.TryReadFile(ggpk, file)!;
    }
    else if (rest.Contains("--restore"))
    {
        string backup = Path.Combine("data", "gateway_list.original.txt");
        if (!File.Exists(backup)) { Console.Error.WriteLine($"[patch] no {backup}"); return 1; }
        GgpkMaintenance.ReplaceFile(ggpk, file, File.ReadAllBytes(backup));
        SaveRootHash(ggpk);
        Console.WriteLine($"[patch] {file} restored from {backup}");
        current = GgpkMaintenance.TryReadFile(ggpk, file)!;
    }
    Console.WriteLine($"[patch] {file}:");
    Console.WriteLine(DecodeList(current).TrimEnd());
    return 0;
}

// A changed GGPK has a new root hash, which the Patch server answers with. Before the first extraction there is no file
// to update (the extraction reads the hash itself).
static void SaveRootHash(string ggpk)
{
    if (!File.Exists(ClientConstants.DefaultFile)) return;
    (ClientConstants.Load() with { GgpkRootHash = Convert.ToHexString(GgpkMaintenance.RootHash(ggpk)) }).Save();
}

static string DecodeList(byte[] b) =>
    b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE ? Encoding.Unicode.GetString(b, 2, b.Length - 2) : Encoding.Unicode.GetString(b);

static string LoadGatewayList()
{
    string? path = Environment.GetEnvironmentVariable("Game_GATEWAY_LIST");
    if (path is not null && File.Exists(path))
        return File.ReadAllText(path, Encoding.Unicode);

    return string.Join("\r\n", new[]
    {
        "Europe;Local (EU);127.0.0.1:20484;127.0.0.1;false",
        "America;Local (US);127.0.0.1:20483;127.0.0.1;false",
    }) + "\r\n";
}

static string? Find(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}
