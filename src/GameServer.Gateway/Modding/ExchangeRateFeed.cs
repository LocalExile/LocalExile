using System.Net.Http.Headers;
using GameServer.Core.Trade;

/// <summary>The rates service a mod asked for (<c>IModContext.UseExchangeRates</c>): while one is asked for, the Gateway keeps
/// <see cref="File"/> up to date from it, for the Currency Exchange module to read. Nothing here runs unless a mod asks, and
/// the Gateway contacts only that address.</summary>
static class ExchangeRateFeed
{
    public const string File = "data/currency-exchange-rates.json";
    const int MaxBytes = 16 * 1024 * 1024;
    static readonly TimeSpan Every = TimeSpan.FromHours(1), AfterFailure = TimeSpan.FromMinutes(5);

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    static readonly List<object> Asked = [];

    /// <summary>The official league whose prices the mod chose; the last one asked wins, so a reloading mod's new choice stands. With none,
    /// the current challenge league's.</summary>
    public static GameServer.Modding.ExchangeRatesLeague League
    {
        get { lock (Asked) return Asked.Count == 0 ? default : (GameServer.Modding.ExchangeRatesLeague)Asked[^1]; }
    }

    public static IDisposable Use(string url, GameServer.Modding.ExchangeRatesLeague league)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new ArgumentException($"not an http address: {url}");
        object ask = league;
        lock (Asked) Asked.Add(ask);
        var stop = new CancellationTokenSource();
        _ = KeepUpdated(address, stop.Token);
        return new Undo(() =>
        {
            lock (Asked) Asked.RemoveAll(a => ReferenceEquals(a, ask));
            stop.Cancel();
            stop.Dispose();
        });
    }

    static async Task KeepUpdated(Uri address, CancellationToken stop)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxBytes };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalExile-Gateway");
        EntityTagHeaderValue? seen = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var wait = Every;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, address);
                    if (seen is not null) request.Headers.IfNoneMatch.Add(seen);
                    using var response = await http.SendAsync(request, stop);
                    if (response.StatusCode != System.Net.HttpStatusCode.NotModified)
                    {
                        response.EnsureSuccessStatusCode();
                        string json = await response.Content.ReadAsStringAsync(stop);
                        // What the service says is not trusted further than this: the module's market checks each trade.
                        if (ExchangeRates.Parse(json) is not { Leagues.Count: > 0 } rates) throw new InvalidDataException("no leagues in the rates");
                        string written = File + ".new";
                        await System.IO.File.WriteAllTextAsync(written, json, stop);
                        System.IO.File.Move(written, File, overwrite: true);
                        seen = response.Headers.ETag;
                        Console.WriteLine($"[rates] {address}: rates of {DateTimeOffset.FromUnixTimeSeconds(rates.SourceHour):u}");
                    }
                }
                catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                    or System.Text.Json.JsonException or InvalidDataException or (TaskCanceledException and not OperationCanceledException { CancellationToken.IsCancellationRequested: true }))
                {
                    Console.WriteLine($"[rates] {address}: {e.Message} (the last rates stay)");
                    wait = AfterFailure;
                }
                await Task.Delay(wait, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
