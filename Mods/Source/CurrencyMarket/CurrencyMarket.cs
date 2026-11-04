using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameServer.Modding;

public sealed class CurrencyMarket : IMod, IDisposable
{
    public sealed record Settings
    {
        public bool FetchPrices { get; init; } = false;
        public string PriceLeague { get; init; } = "Standard";
        public string[] LocalLeagues { get; init; } = ["Standard"];
        public double RefreshMinutes { get; init; } = 60;
        public decimal GapPercent { get; init; } = 10;
        public decimal LevelGapPercent { get; init; } = 2;
        public decimal VolumeFactor { get; init; } = 0.05m;
        public decimal LevelVolumeMultiplier { get; init; } = 1.25m;
    }
    const int OrdersPerSide = 6, MaxCurrencyUnits = 10000;
    static readonly string[] Categories = ["Currency", "Fragment"];
    public sealed record Price(string Name, decimal Value, decimal Volume);
    public sealed record Feed(string Category, string? ETag, string Body, DateTimeOffset? RefreshAfter = null);
    public sealed record Cache(string League, DateTimeOffset Fetched, Feed[] Feeds);
    readonly CancellationTokenSource _stop = new();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    IModContext _mod = null!;
    IExchangeMarket _market = null!;
    Settings _settings = null!;
    Cache? _cache;
    Task? _worker;
    string _status = "Starting";
    string _published = "";
    Cache? _pricedCache;
    ExchangeQuote[] _quotes = [];
    string _priceKey = "";
    string? _fetchError;
    const string Root = "data/mods/currencymarket";

    public void Load(IModContext mod)
    {
        _mod = mod;
        Directory.CreateDirectory(Root);
        string file = Path.Combine(Root, "settings.json");
        if (!File.Exists(file)) Save(file, new Settings(), MarketJsonContext.Default.Settings);
        _settings = JsonSerializer.Deserialize(File.ReadAllText(file), MarketJsonContext.Default.Settings) ?? throw new InvalidDataException("Empty settings");
        Validate(_settings);
        _market = mod.RegisterExchangeMarket();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalExile-CurrencyMarket/1.0 (+https://github.com/localexile/localexile)");
        string priceFile = Path.Combine(Root, "prices.json");
        if (!File.Exists(priceFile)) File.Copy(Path.Combine(mod.Folder, "prices.default.json"), priceFile);
        if (File.Exists(priceFile))
        {
            try
            {
                var cache = JsonSerializer.Deserialize(File.ReadAllText(priceFile), MarketJsonContext.Default.Cache);
                if (cache?.League == _settings.PriceLeague && cache.Feeds.Select(f => f.Category).Order().SequenceEqual(Categories.Order()))
                { Parse(cache); _cache = cache; }
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException or KeyNotFoundException or InvalidOperationException)
            { mod.Log("Ignoring invalid price cache: " + e.Message); }
        }
        mod.OnChatCommand("cx_market", "/cx_market (market source, cache age and order count)", c => c.Player.Tell(Volatile.Read(ref _status)));
        _worker = Task.Run(Run);
    }
    static void Validate(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.PriceLeague) || s.LocalLeagues is null || s.LocalLeagues.Any(string.IsNullOrWhiteSpace)
            || s.RefreshMinutes < 15 || !double.IsFinite(s.RefreshMinutes)
            || s.RefreshMinutes > 525600 || s.GapPercent is < 0 or >= 100
            || s.VolumeFactor is < 0 or > 100 || s.LevelGapPercent is <= 0 or > 50
            || s.GapPercent / 2 + (OrdersPerSide - 1) * s.LevelGapPercent >= 100
            || s.LevelVolumeMultiplier is <= 0 or > 2)
            throw new InvalidDataException("Invalid currency market settings; see README.md");
    }
    async Task Run()
    {
        DateTimeOffset nextFetch = _cache is null ? DateTimeOffset.MinValue : NextRefresh(_cache, _settings);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Publish();
                if (_settings.FetchPrices && DateTimeOffset.UtcNow >= nextFetch)
                {
                    try
                    {
                        var feeds = new List<Feed>();
                        foreach (string category in Categories)
                        {
                            var old = _cache?.Feeds.FirstOrDefault(f => f.Category == category);
                            using var request = new HttpRequestMessage(HttpMethod.Get,
                                $"https://poe.ninja/poe1/api/economy/exchange/current/overview?league={Uri.EscapeDataString(_settings.PriceLeague)}&type={Uri.EscapeDataString(category)}");
                            if (old?.ETag is { } tag) request.Headers.TryAddWithoutValidation("If-None-Match", tag);
                            using var response = await _http.SendAsync(request, _stop.Token);
                            // Keep the service's validator verbatim, including weak unquoted tags.
                            string? etag = response.Headers.NonValidated.TryGetValues("ETag", out var tags)
                                ? tags.FirstOrDefault() : null;
                            DateTimeOffset? refreshAfter = response.Headers.CacheControl?.MaxAge is { } maxAge
                                ? DateTimeOffset.UtcNow + (maxAge > response.Headers.Age.GetValueOrDefault()
                                    ? maxAge - response.Headers.Age.GetValueOrDefault() : TimeSpan.Zero) : null;
                            if (response.StatusCode == HttpStatusCode.NotModified && old is not null)
                                feeds.Add(old with { ETag = etag ?? old.ETag, RefreshAfter = refreshAfter });
                            else
                            {
                                response.EnsureSuccessStatusCode();
                                feeds.Add(new(category, etag, await response.Content.ReadAsStringAsync(_stop.Token), refreshAfter));
                            }
                        }
                        var cache = new Cache(_settings.PriceLeague, DateTimeOffset.UtcNow, feeds.ToArray());
                        Parse(cache);
                        Save(Path.Combine(Root, "prices.json"), cache, MarketJsonContext.Default.Cache);
                        _cache = cache;
                        _fetchError = null;
                        _mod.Log($"Price refresh succeeded: {cache.League}, {cache.Feeds.Length} categories.");
                        Publish();
                    }
                    catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
                    {
                        if (_stop.IsCancellationRequested) break;
                        _fetchError = e.Message;
                        _mod.Log("Price refresh failed; retaining last valid cache: " + e.Message);
                    }
                    nextFetch = _cache is not null && _fetchError is null ? NextRefresh(_cache, _settings)
                        : DateTimeOffset.UtcNow.AddMinutes(_settings.RefreshMinutes);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested || !_mod.Lifetime.IsActive) { }
        catch (Exception e) { _status = "Currency market stopped: " + e.Message; _mod.Log(_status); }
    }
    public static DateTimeOffset NextRefresh(Cache cache, Settings settings) => cache.Feeds
        .Select(f => f.RefreshAfter ?? DateTimeOffset.MinValue)
        .Append(cache.Fetched.AddMinutes(settings.RefreshMinutes)).Max();
    public static Price[] Parse(Cache cache)
    {
        var prices = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in cache.Feeds)
        {
            using var doc = JsonDocument.Parse(feed.Body);
            var root = doc.RootElement;
            if (root.GetProperty("core").GetProperty("primary").GetString() != "chaos")
                throw new InvalidDataException("Expected prices denominated in Chaos Orbs");
            var names = root.GetProperty("core").GetProperty("items").EnumerateArray()
                .Concat(root.GetProperty("items").EnumerateArray()).DistinctBy(i => i.GetProperty("id").GetString()).ToDictionary(
                i => i.GetProperty("id").GetString()!, i => i.GetProperty("name").GetString()!);
            foreach (var line in root.GetProperty("lines").EnumerateArray())
            {
                if (!names.TryGetValue(line.GetProperty("id").GetString()!, out string? name)
                    || !line.GetProperty("primaryValue").TryGetDecimal(out decimal value) || value <= 0 || value > 999999
                    || !line.GetProperty("volumePrimaryValue").TryGetDecimal(out decimal volume) || volume <= 0 || volume > 1000000000000m) continue;
                prices[name] = new(name, value, volume / value);
            }
        }
        if (prices.Count == 0) throw new InvalidDataException("No usable currency prices");
        return prices.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    }
    public static ExchangeQuote[] Quotes(IEnumerable<Price> prices, Settings settings)
    {
        Validate(settings);
        var quotes = new List<ExchangeQuote>();
        foreach (var price in prices.Where(p => p.Name != "Chaos Orb" && p.Volume > 0))
        {
            AddSide(true, settings.VolumeFactor);
            AddSide(false, settings.VolumeFactor);
            void AddSide(bool buy, decimal factor)
            {
                if (factor == 0) return;
                int previousLot = 0, previousChaos = 0;
                for (int level = 0; level < OrdersPerSide; level++)
                {
                    decimal gap = (settings.GapPercent / 2)
                        + level * settings.LevelGapPercent;
                    decimal value = price.Value * (buy ? 1 - gap / 100 : 1 + gap / 100);
                    if (value <= 0 || value > 65000) continue;
                    int units = (int)Math.Min(MaxCurrencyUnits, decimal.Floor(price.Volume * factor));
                    factor *= settings.LevelVolumeMultiplier;
                    int lot = (int)Math.Clamp(buy ? decimal.Ceiling(1 / value) : decimal.Floor(1 / value), 1, 65000);
                    int chaos = (int)(buy ? decimal.Floor(value * lot) : decimal.Ceiling(value * lot));
                    if (chaos <= 0 || chaos > 65000) continue;
                    // Integer rounding must not merge adjacent levels in the client's depth view.
                    if (previousLot != 0 && (buy
                        ? (long)chaos * previousLot >= (long)previousChaos * lot
                        : (long)chaos * previousLot <= (long)previousChaos * lot))
                    {
                        if (buy && previousChaos > 1) { lot = previousLot; chaos = previousChaos - 1; }
                        else if (buy) { lot = previousLot + 1; chaos = previousChaos; }
                        else if (previousLot > 1) { lot = previousLot - 1; chaos = previousChaos; }
                        else { lot = previousLot; chaos = previousChaos + 1; }
                    }
                    int gcd = Gcd(lot, chaos); lot /= gcd; chaos /= gcd;
                    if (lot > 65000 || chaos > 65000) continue;
                    int cap = Math.Min(MaxCurrencyUnits / lot, 999999 / Math.Max(lot, chaos));
                    int lots = Math.Min(cap, units / lot);
                    if (lots == 0) continue;
                    quotes.Add(buy ? new(price.Name, "Chaos Orb", lot * lots, chaos * lots)
                        : new("Chaos Orb", price.Name, chaos * lots, lot * lots));
                    previousLot = lot; previousChaos = chaos;
                }
            }
        }
        return quotes.ToArray();
    }
    static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
    void Publish()
    {
        if (_cache is null)
        {
            _status = "Currency market: no price cache; " + (_settings.FetchPrices ? "waiting for a successful fetch." : "online fetching is disabled.");
            if (_fetchError is not null) _status += " Last refresh failed: " + _fetchError;
            return;
        }
        if (!ReferenceEquals(_pricedCache, _cache))
        {
            _quotes = Quotes(Parse(_cache), _settings);
            _priceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(_quotes, MarketJsonContext.Default.ExchangeQuoteArray))));
            _pricedCache = _cache;
        }
        var quotes = _quotes;
        long epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)(_settings.RefreshMinutes * 60);
        string key = _priceKey + ":" + epoch;
        if (key != _published)
        {
            _market.Publish(key, quotes, _settings.LocalLeagues);
            _published = key;
            _mod.Log($"Published {quotes.Length} finite buy/sell quotes; applied on the next game tick.");
        }
        _status = $"Currency market: poe.ninja / {_settings.PriceLeague}, {quotes.Length} generated quotes; cache checked {_cache.Fetched:u}; up to {OrdersPerSide} levels/side, gap {_settings.GapPercent}%, step {_settings.LevelGapPercent}%; volume {_settings.VolumeFactor}, growth {_settings.LevelVolumeMultiplier}; refresh {_settings.RefreshMinutes} min.";
        if (!_settings.FetchPrices) _status += " Online fetching disabled; using disk cache.";
        if (_fetchError is not null) _status += " Last refresh failed; cached prices retained: " + _fetchError;
    }
    static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, type));
        File.Move(path + ".tmp", path, true);
    }
    public void Dispose()
    {
        _stop.Cancel();
        _worker?.GetAwaiter().GetResult();
        _worker = null;
        _market?.Dispose();
        _http.Dispose();
        _stop.Dispose();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CurrencyMarket.Settings))]
[JsonSerializable(typeof(CurrencyMarket.Cache))]
[JsonSerializable(typeof(ExchangeQuote[]))]
internal partial class MarketJsonContext : JsonSerializerContext;
