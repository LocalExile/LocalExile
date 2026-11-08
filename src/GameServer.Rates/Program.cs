using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameServer.Core.Trade;

// The rates service (Agents/docs/protocol/29-currency-exchange.md): it reads GGG's public hourly Currency Exchange digests, one
// request an hour however many Gateways there are, and serves one small file of prices.
// Game_RATES_PORT (default 6114), Game_RATES_FOLDER (default rates-data), Game_RATES_HOURS (default 168). With --once <file>: writes the file and exits.
int hours = int.Parse(Environment.GetEnvironmentVariable("Game_RATES_HOURS") ?? "168");
const string Digests = "https://web.poecdn.com/api/currency-exchange/";

string folder = Environment.GetEnvironmentVariable("Game_RATES_FOLDER") ?? "rates-data";
int port = int.Parse(Environment.GetEnvironmentVariable("Game_RATES_PORT") ?? "6114");
Directory.CreateDirectory(folder);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalExile-rates/1.0");
http.DefaultRequestHeaders.UserAgent.ParseAdd("(+https://github.com/LocalExile/LocalExile)");

string? Cached(long hour) => Path.Combine(folder, $"digest-{hour}.json") is var path && File.Exists(path) ? path : null;

// Keeps the digests of the last hours, newest asked for first; each is fixed once its hour has passed, so it is asked for once.
// GGG may remove old history: an hour it refuses is skipped, and the rest still count.
async Task<string?> Update(CancellationToken stop)
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600, newest = now - 3600, oldest = now - hours * 3600;
    for (long hour = newest; hour >= oldest; hour -= 3600)
    {
        if (Cached(hour) is not null) continue;
        using var response = await http.GetAsync(Digests + hour, stop);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1), stop);
            break;
        }
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[rates] hour {hour}: {(int)response.StatusCode}, skipped");
            continue;
        }
        string digest = await response.Content.ReadAsStringAsync(stop);
        if (digest.Contains($"\"next_change_id\":{hour + 3600}", StringComparison.Ordinal)) // else not published yet
            await File.WriteAllTextAsync(Path.Combine(folder, $"digest-{hour}.json"), digest, stop);
        await Task.Delay(TimeSpan.FromSeconds(1), stop);
    }
    foreach (string file in Directory.GetFiles(folder, "digest-*.json"))
        if (long.TryParse(Path.GetFileNameWithoutExtension(file)["digest-".Length..], out long hour) && hour < oldest) File.Delete(file);
    string[] held = [.. Directory.GetFiles(folder, "digest-*.json").OrderDescending(StringComparer.Ordinal)];
    return held.Length == 0 ? null : ExchangeRates.FromDigests(held.Select(File.ReadAllText)).Serialize();
}

if (args is ["--once", var output])
{
    if (await Update(CancellationToken.None) is not { } once) return 1;
    await File.WriteAllTextAsync(output, once);
    Console.WriteLine($"[rates] wrote {output}, {once.Length} characters");
    return 0;
}

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

byte[]? served = null;
string tag = "";
string latest = Path.Combine(folder, "rates.json");
if (File.Exists(latest)) Serve(File.ReadAllText(latest));

void Serve(string json)
{
    served = Encoding.UTF8.GetBytes(json);
    tag = $"\"{Convert.ToHexString(SHA256.HashData(served))[..16]}\"";
}

var updating = Task.Run(async () =>
{
    while (!stopping.IsCancellationRequested)
    {
        var wait = TimeSpan.FromMinutes(5);
        try
        {
            if (await Update(stopping.Token) is { } json)
            {
                await File.WriteAllTextAsync(latest, json, stopping.Token);
                Serve(json);
                Console.WriteLine($"[rates] {json.Length} characters, {Directory.GetFiles(folder, "digest-*.json").Length} digests");
            }
            // Just past the next hour: its digest is published when the hour ends.
            long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            wait = TimeSpan.FromSeconds(3600 - second % 3600 + 300);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or KeyNotFoundException or InvalidOperationException or (TaskCanceledException and not OperationCanceledException { CancellationToken.IsCancellationRequested: true }))
        {
            Console.WriteLine($"[rates] {e.Message}");
        }
        await Task.Delay(wait, stopping.Token);
    }
}, stopping.Token);

using var listener = new HttpListener();
listener.Prefixes.Add($"http://+:{port}/");
listener.Start();
Console.WriteLine($"[rates] serving /rates.json on port {port}");
stopping.Token.Register(listener.Stop);
while (!stopping.IsCancellationRequested)
{
    HttpListenerContext context;
    try { context = await listener.GetContextAsync(); }
    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
    _ = Task.Run(async () =>
    {
        using var response = context.Response;
        try
        {
            var body = served;
            if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/rates.json" || body is null) response.StatusCode = 404;
            else if (context.Request.Headers["If-None-Match"] == tag) response.StatusCode = 304;
            else
            {
                response.ContentType = "application/json";
                response.AddHeader("ETag", tag);
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body);
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
    });
}
try { await updating; }
catch (OperationCanceledException) { }
return 0;
